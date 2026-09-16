// Streams and the File API's reader, as one asset because they are one slice: Blob.prototype.
// stream() is the reason a real ReadableStream had to exist, and FileReader is the other half of
// what a page does with a Blob's bytes.
//
// Written in JavaScript rather than as host functions because the specification is written that way
// — the queue, the pending read requests and the pull back-pressure are a state machine over
// promises, and expressing it in C# would mean re-deriving the promise plumbing the engine already
// has. The one thing the host provides is a Blob's bytes, which live where blobs do.
//
// NOT implemented, and detectably so: pipeTo and pipeThrough, which need a WritableStream, and BYOB
// readers, which need a byte-stream controller. Each is its own capability rather than a piece of
// this one; getReader({mode: 'byob'}) throws rather than handing back a reader that is not one.
// Async iteration — values() and @@asyncIterator — is implemented; it was the one piece held back on
// an engine fix, and that fix is live in the pinned Broiler.JS pointer.
//
// FileReader and ProgressEvent are the other half of this slice, in file-reader.js; PolyfillAssets
// joins the two into the one script that is evaluated.
(function () {
    'use strict';

    // The host hands bytes over as an ArrayBuffer and the view is made here. Doing it in JavaScript
    // rather than host-side keeps one conversion for every caller and does not depend on the host
    // resolving the realm's Uint8Array — which is the sort of lookup that succeeds in one call site
    // and quietly returns the bare buffer in another.
    function asBytes(buffer) {
        return buffer instanceof Uint8Array ? buffer : new Uint8Array(buffer);
    }

    // ---------------------------------------------------------------- ReadableStream

    var streamState = new WeakMap();      // stream -> state
    var readerStream = new WeakMap();     // reader -> state (null once released)
    var controllerStream = new WeakMap(); // controller -> state

    function typeError(message) { return new TypeError(message); }

    function stateOf(map, object, member, interfaceName) {
        var state = map.get(object);
        if (state === undefined)
            throw typeError("Failed to execute '" + member + "' on '" + interfaceName + "': Illegal invocation");
        return state;
    }

    function ReadableStream(underlyingSource, strategy) {
        if (!new.target) {
            throw typeError("Failed to construct 'ReadableStream': Please use the 'new' operator, " +
                'this DOM object constructor cannot be called as a function.');
        }

        var source = underlyingSource || {};
        if (source.type !== undefined && source.type !== null && String(source.type) !== '') {
            // A byte stream needs a ReadableByteStreamController, which this does not have. Refusing
            // is the honest answer: accepting the option and building a default controller would
            // hand back a stream whose byobRequest never appears.
            throw typeError("Failed to construct 'ReadableStream': Only the default reader is " +
                'supported; byte streams (type: "bytes") are not implemented.');
        }

        var state = {
            stream: this,
            state: 'readable',
            storedError: undefined,
            queue: [],
            readRequests: [],
            reader: null,
            source: source,
            closeRequested: false,
            pulling: false,
            pullAgain: false,
            highWaterMark: strategy && typeof strategy.highWaterMark === 'number' ? strategy.highWaterMark : 1,
        };
        streamState.set(this, state);

        var controller = Object.create(ReadableStreamDefaultController.prototype);
        controllerStream.set(controller, state);
        state.controller = controller;

        if (typeof source.start === 'function') {
            try {
                source.start(controller);
            } catch (error) {
                errorStream(state, error);
                return;
            }
        }

        pullIfNeeded(state);
    }

    function errorStream(state, error) {
        if (state.state !== 'readable')
            return;

        state.state = 'errored';
        state.storedError = error;
        state.queue.length = 0;

        var requests = state.readRequests;
        state.readRequests = [];
        for (var i = 0; i < requests.length; i++)
            requests[i].reject(error);

        if (state.closedReject)
            state.closedReject(error);
    }

    function closeStream(state) {
        if (state.state !== 'readable')
            return;

        state.state = 'closed';
        var requests = state.readRequests;
        state.readRequests = [];
        for (var i = 0; i < requests.length; i++)
            requests[i].resolve({ value: undefined, done: true });

        if (state.closedResolve)
            state.closedResolve(undefined);
    }

    // The back-pressure signal: pull again while there is a waiting reader or the queue is under the
    // high-water mark, one call at a time. Without the pulling/pullAgain pair a source whose pull
    // enqueues synchronously would recurse.
    function pullIfNeeded(state) {
        if (state.state !== 'readable' || state.closeRequested || typeof state.source.pull !== 'function')
            return;
        if (state.readRequests.length === 0 && state.queue.length >= state.highWaterMark)
            return;

        if (state.pulling) {
            state.pullAgain = true;
            return;
        }

        state.pulling = true;
        var result;
        try {
            result = state.source.pull(state.controller);
        } catch (error) {
            state.pulling = false;
            errorStream(state, error);
            return;
        }

        Promise.resolve(result).then(function () {
            state.pulling = false;
            if (state.pullAgain) {
                state.pullAgain = false;
                pullIfNeeded(state);
            }
        }, function (error) {
            state.pulling = false;
            errorStream(state, error);
        });
    }

    function cancelStream(state, reason) {
        if (state.state === 'closed')
            return Promise.resolve(undefined);
        if (state.state === 'errored')
            return Promise.reject(state.storedError);

        state.queue.length = 0;
        var result;
        try {
            result = typeof state.source.cancel === 'function' ? state.source.cancel(reason) : undefined;
        } catch (error) {
            return Promise.reject(error);
        }

        closeStream(state);
        return Promise.resolve(result).then(function () { return undefined; });
    }

    function ReadableStreamDefaultController() {
        throw typeError("Failed to construct 'ReadableStreamDefaultController': Illegal constructor");
    }

    ReadableStreamDefaultController.prototype.enqueue = function (chunk) {
        var state = stateOf(controllerStream, this, 'enqueue', 'ReadableStreamDefaultController');
        if (state.closeRequested || state.state !== 'readable') {
            throw typeError("Failed to execute 'enqueue' on 'ReadableStreamDefaultController': " +
                'Cannot enqueue a chunk into a readable stream that is closed or has been requested to be closed');
        }

        if (state.readRequests.length > 0)
            state.readRequests.shift().resolve({ value: chunk, done: false });
        else
            state.queue.push(chunk);

        pullIfNeeded(state);
    };

    ReadableStreamDefaultController.prototype.close = function () {
        var state = stateOf(controllerStream, this, 'close', 'ReadableStreamDefaultController');
        if (state.closeRequested || state.state !== 'readable') {
            throw typeError("Failed to execute 'close' on 'ReadableStreamDefaultController': " +
                'Cannot close a readable stream that has already been requested to be closed');
        }

        state.closeRequested = true;
        if (state.queue.length === 0)
            closeStream(state);
    };

    ReadableStreamDefaultController.prototype.error = function (error) {
        errorStream(stateOf(controllerStream, this, 'error', 'ReadableStreamDefaultController'), error);
    };

    Object.defineProperty(ReadableStreamDefaultController.prototype, 'desiredSize', {
        get: function () {
            var state = stateOf(controllerStream, this, 'desiredSize', 'ReadableStreamDefaultController');
            if (state.state === 'errored') return null;
            if (state.state === 'closed') return 0;
            return state.highWaterMark - state.queue.length;
        },
        enumerable: true, configurable: true,
    });

    function ReadableStreamDefaultReader() {
        throw typeError("Failed to construct 'ReadableStreamDefaultReader': Illegal constructor");
    }

    function acquireReader(state) {
        var reader = Object.create(ReadableStreamDefaultReader.prototype);
        readerStream.set(reader, state);
        state.reader = reader;

        state.closedPromise = new Promise(function (resolve, reject) {
            state.closedResolve = resolve;
            state.closedReject = reject;
        });
        // A closed promise nothing observes is a rejection nothing handles; keep the engine from
        // reporting one for a stream a page simply never asked about.
        state.closedPromise.catch(function () { });

        if (state.state === 'closed') state.closedResolve(undefined);
        else if (state.state === 'errored') state.closedReject(state.storedError);

        return reader;
    }

    ReadableStreamDefaultReader.prototype.read = function () {
        var state = readerStream.get(this);
        if (state === undefined)
            return Promise.reject(typeError("Failed to execute 'read' on 'ReadableStreamDefaultReader': Illegal invocation"));
        if (state === null) {
            return Promise.reject(typeError("Failed to execute 'read' on 'ReadableStreamDefaultReader': " +
                'This readable stream reader has been released and cannot be used to read from its previous owner stream'));
        }

        if (state.queue.length > 0) {
            var chunk = state.queue.shift();
            if (state.closeRequested && state.queue.length === 0)
                closeStream(state);
            return Promise.resolve({ value: chunk, done: false });
        }

        if (state.state === 'closed')
            return Promise.resolve({ value: undefined, done: true });
        if (state.state === 'errored')
            return Promise.reject(state.storedError);

        var request = {};
        var promise = new Promise(function (resolve, reject) {
            request.resolve = resolve;
            request.reject = reject;
        });
        state.readRequests.push(request);
        pullIfNeeded(state);
        return promise;
    };

    ReadableStreamDefaultReader.prototype.cancel = function (reason) {
        var state = readerStream.get(this);
        if (!state)
            return Promise.reject(typeError("Failed to execute 'cancel' on 'ReadableStreamDefaultReader': Illegal invocation"));
        return cancelStream(state, reason);
    };

    ReadableStreamDefaultReader.prototype.releaseLock = function () {
        var state = readerStream.get(this);
        if (!state)
            return;

        var pending = state.readRequests;
        state.readRequests = [];
        for (var i = 0; i < pending.length; i++) {
            pending[i].reject(typeError('This readable stream reader has been released and cannot be used ' +
                'to read from its previous owner stream'));
        }

        state.reader = null;
        readerStream.set(this, null);
    };

    Object.defineProperty(ReadableStreamDefaultReader.prototype, 'closed', {
        get: function () {
            var state = readerStream.get(this);
            if (!state)
                return Promise.reject(typeError('Illegal invocation'));
            return state.closedPromise;
        },
        enumerable: true, configurable: true,
    });

    Object.defineProperty(ReadableStream.prototype, 'locked', {
        get: function () {
            return stateOf(streamState, this, 'locked', 'ReadableStream').reader !== null;
        },
        enumerable: true, configurable: true,
    });

    ReadableStream.prototype.getReader = function (options) {
        var state = stateOf(streamState, this, 'getReader', 'ReadableStream');
        if (options && options.mode !== undefined && options.mode !== null && String(options.mode) !== '') {
            if (String(options.mode) !== 'byob') {
                throw typeError("Failed to execute 'getReader' on 'ReadableStream': " +
                    "The provided value '" + options.mode + "' is not a valid enum value of type ReadableStreamReaderMode.");
            }
            // A BYOB reader reads into a caller-supplied buffer and needs a byte-stream controller.
            // Refusing names the missing capability instead of handing back a default reader that
            // would silently ignore the buffer.
            throw typeError("Failed to execute 'getReader' on 'ReadableStream': " +
                'BYOB readers are not implemented; this stream has a default controller.');
        }

        if (state.reader !== null) {
            throw typeError("Failed to execute 'getReader' on 'ReadableStream': " +
                'ReadableStreamDefaultReader constructor can only accept readable streams that are not yet locked to a reader');
        }

        return acquireReader(state);
    };

    ReadableStream.prototype.cancel = function (reason) {
        var state = streamState.get(this);
        if (!state)
            return Promise.reject(typeError("Failed to execute 'cancel' on 'ReadableStream': Illegal invocation"));
        if (state.reader !== null) {
            return Promise.reject(typeError("Failed to execute 'cancel' on 'ReadableStream': " +
                'Cannot cancel a stream locked by a reader'));
        }
        return cancelStream(state, reason);
    };

    // tee() reads the source once and fans the chunks out to two streams, which is what makes the
    // copies independent without reading the source twice. Both branches share one reader, so the
    // original is locked from here on — measured, and the reason a page tees rather than reading
    // twice.
    ReadableStream.prototype.tee = function () {
        var state = stateOf(streamState, this, 'tee', 'ReadableStream');
        var reader = this.getReader();
        var controllers = [];
        var reading = false;
        var cancelled = [false, false];

        function pump() {
            if (reading)
                return;
            reading = true;
            reader.read().then(function (result) {
                reading = false;
                if (result.done) {
                    for (var i = 0; i < controllers.length; i++) {
                        if (!cancelled[i]) controllers[i].close();
                    }
                    return;
                }

                for (var j = 0; j < controllers.length; j++) {
                    if (!cancelled[j]) controllers[j].enqueue(result.value);
                }
            }, function (error) {
                reading = false;
                for (var k = 0; k < controllers.length; k++)
                    controllers[k].error(error);
            });
        }

        function branch(index) {
            return new ReadableStream({
                start: function (controller) { controllers[index] = controller; },
                pull: function () { pump(); },
                cancel: function (reason) {
                    cancelled[index] = true;
                    if (cancelled[0] && cancelled[1])
                        return cancelStream(state, reason);
                    return undefined;
                },
            });
        }

        return [branch(0), branch(1)];
    };

    // values() and @@asyncIterator, so `for await (const chunk of response.body)` works.
    //
    // These were held back for a while, and the reason is worth keeping: `for await` used to
    // deadlock the agent whenever the iterator's next() handed back a promise that was not
    // *already* settled — the engine blocked the one thread allowed to run this context's
    // JavaScript, so the job that would settle the promise could never run. `reader.read().then(…)`
    // below is exactly that shape, so installing the hook would have turned the ordinary line above
    // from a TypeError a page's script survives into a capture that never settles. The engine fix
    // ("Stop for-await deadlocking on a step result that is not already settled") is upstream and
    // the pinned Broiler.JS pointer carries it, so the hold is released.
    //
    // next() deliberately releases the lock on done rather than on the *next* call, so a loop that
    // runs to completion leaves the stream unlocked; return() — which the engine calls when a loop
    // is left by break, return or throw — cancels unless preventCancel was asked for, then releases.
    ReadableStream.prototype.values = function (options) {
        var reader = this.getReader();
        var preventCancel = !!(options && options.preventCancel);
        var iterator = {
            next: function () {
                return reader.read().then(function (result) {
                    if (result.done)
                        reader.releaseLock();
                    return result;
                });
            },
            'return': function (value) {
                if (!preventCancel)
                    reader.cancel(value);
                reader.releaseLock();
                return Promise.resolve({ value: value, done: true });
            },
        };
        iterator[Symbol.asyncIterator] = function () { return this; };
        return iterator;
    };

    ReadableStream.prototype[Symbol.asyncIterator] = ReadableStream.prototype.values;

    globalThis.ReadableStream = ReadableStream;
    globalThis.ReadableStreamDefaultReader = ReadableStreamDefaultReader;
    globalThis.ReadableStreamDefaultController = ReadableStreamDefaultController;

    // A stream over a fixed byte array: one chunk, then close. Used by Blob.prototype.stream() and
    // by a fetch body, so both hand back the same interface a page's own `new ReadableStream` does.
    function streamOverBytes(buffer) {
        var bytes = asBytes(buffer);
        return new ReadableStream({
            start: function (controller) {
                if (bytes.length > 0)
                    controller.enqueue(bytes);
                controller.close();
            },
        });
    }

    // The same stream, but one that reports the first read or cancel. A fetch body needs it: the
    // Body mixin's `bodyUsed` is "the body has been disturbed", and text()/json()/clone() refuse once
    // it is. The report comes from `pull` rather than from a wrapper on the instance, so the stream a
    // page holds has no own properties of its own — and the high-water mark is zero precisely so the
    // constructor does not pull, which would mark the body used before anything read it.
    function streamOverObservedBytes(buffer, onDisturbed) {
        var bytes = asBytes(buffer);
        return new ReadableStream({
            pull: function (controller) {
                onDisturbed();
                if (bytes.length > 0)
                    controller.enqueue(bytes);
                controller.close();
            },
            cancel: function () { onDisturbed(); },
        }, { highWaterMark: 0 });
    }

    globalThis.__broilerStreamOverBytes = streamOverBytes;
    globalThis.__broilerStreamOverObservedBytes = streamOverObservedBytes;
    // `locked` is a prototype accessor, so asking for it from the host means invoking a getter with
    // the right receiver. Answering it here keeps that in JavaScript, where `this` is unambiguous.
    globalThis.__broilerStreamIsLocked = function (stream) {
        return !!(stream && streamState.get(stream) && streamState.get(stream).reader !== null);
    };
})();
