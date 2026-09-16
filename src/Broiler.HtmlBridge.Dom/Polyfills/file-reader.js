// FileReader and ProgressEvent — the File API's reader, and the event interface it fires. The
// streams half of this slice is streams.js, which PolyfillAssets evaluates first, in the same script.
//
// __broilerBlobBytes is the host hook; it is captured into this closure and deleted from the global
// so a page cannot reach a blob's bytes out of band.
(function () {
    'use strict';

    var hostBlobBytes = globalThis.__broilerBlobBytes;
    delete globalThis.__broilerBlobBytes;

    // The host hands bytes over as an ArrayBuffer and the view is made here. Doing it in JavaScript
    // rather than host-side keeps one conversion for every caller and does not depend on the host
    // resolving the realm's Uint8Array — which is the sort of lookup that succeeds in one call site
    // and quietly returns the bare buffer in another.
    function asBytes(buffer) {
        return buffer instanceof Uint8Array ? buffer : new Uint8Array(buffer);
    }

    function blobBytes(blob) { return asBytes(hostBlobBytes(blob)); }

    function typeError(message) { return new TypeError(message); }

    // ---------------------------------------------------------------- ProgressEvent

    // FileReader's events are ProgressEvents, and the interface did not exist. It is a real class
    // here rather than a plain object with the right fields, because `e.constructor.name` and
    // `e instanceof ProgressEvent` are both things a handler reads.
    function ProgressEvent(type, init) {
        if (!new.target) {
            throw typeError("Failed to construct 'ProgressEvent': Please use the 'new' operator, " +
                'this DOM object constructor cannot be called as a function.');
        }
        init = init || {};
        this.type = String(type);
        this.lengthComputable = !!init.lengthComputable;
        this.loaded = typeof init.loaded === 'number' ? init.loaded : 0;
        this.total = typeof init.total === 'number' ? init.total : 0;
        this.bubbles = !!init.bubbles;
        this.cancelable = !!init.cancelable;
        this.target = null;
        this.currentTarget = null;
        this.defaultPrevented = false;
    }

    ProgressEvent.prototype.preventDefault = function () { this.defaultPrevented = true; };
    ProgressEvent.prototype.stopPropagation = function () { };
    ProgressEvent.prototype.stopImmediatePropagation = function () { };

    // Linked to Event.prototype when the realm has one, so `e instanceof Event` answers through the
    // chain. Event itself is not yet a real interface here, so this is a link rather than a subclass.
    if (typeof Event === 'function' && Event.prototype)
        Object.setPrototypeOf(ProgressEvent.prototype, Event.prototype);

    globalThis.ProgressEvent = ProgressEvent;

    // ---------------------------------------------------------------- FileReader

    var readerState = new WeakMap();

    function FileReader() {
        if (!new.target) {
            throw typeError("Failed to construct 'FileReader': Please use the 'new' operator, " +
                'this DOM object constructor cannot be called as a function.');
        }
        readerState.set(this, {
            readyState: 0,
            result: null,
            error: null,
            aborted: false,
            listeners: Object.create(null),
        });
    }

    FileReader.EMPTY = 0;
    FileReader.LOADING = 1;
    FileReader.DONE = 2;

    function readerStateOf(reader, member) {
        var state = readerState.get(reader);
        if (state === undefined)
            throw typeError("Failed to execute '" + member + "' on 'FileReader': Illegal invocation");
        return state;
    }

    function fire(reader, state, type, loaded, total) {
        var event = new ProgressEvent(type, {
            lengthComputable: total > 0,
            loaded: loaded,
            total: total,
        });
        event.target = reader;
        event.currentTarget = reader;

        var handler = reader['on' + type];
        if (typeof handler === 'function') {
            try { handler.call(reader, event); } catch (ignored) { }
        }

        var listeners = state.listeners[type];
        if (!listeners)
            return;

        // Snapshot: a handler may add or remove one while it runs.
        var snapshot = listeners.slice();
        for (var i = 0; i < snapshot.length; i++) {
            try { snapshot[i].call(reader, event); } catch (ignored) { }
        }
    }

    // The read algorithm, shared by all four readAs* methods: they differ only in how the bytes
    // become a result. The work is deferred to a microtask because a FileReader is asynchronous by
    // definition — a page attaches its handlers after calling readAs*, and doing the work
    // synchronously would deliver `load` to nobody.
    function startRead(reader, blob, member, convert, argumentCount) {
        var state = readerStateOf(reader, member);
        if (argumentCount === 0) {
            throw typeError("Failed to execute '" + member + "' on 'FileReader': " +
                '1 argument required, but only 0 present.');
        }
        if (state.readyState === 1) {
            var busy = new DOMException("Failed to execute '" + member + "' on 'FileReader': " +
                'The object is already busy reading Blobs.', 'InvalidStateError');
            throw busy;
        }

        state.readyState = 1;
        state.result = null;
        state.error = null;
        state.aborted = false;

        Promise.resolve().then(function () {
            if (state.aborted)
                return;

            var bytes;
            try {
                bytes = blobBytes(blob);
            } catch (error) {
                state.readyState = 2;
                state.error = new DOMException('The blob could not be read.', 'NotReadableError');
                fire(reader, state, 'error', 0, 0);
                fire(reader, state, 'loadend', 0, 0);
                return;
            }

            var total = bytes.length;
            fire(reader, state, 'loadstart', 0, total);
            if (state.aborted)
                return;

            fire(reader, state, 'progress', total, total);
            if (state.aborted)
                return;

            state.result = convert(bytes, blob);
            state.readyState = 2;
            fire(reader, state, 'load', total, total);
            fire(reader, state, 'loadend', total, total);
        });
    }

    FileReader.prototype.readAsText = function (blob, encoding) {
        // The encoding argument is accepted and ignored: this engine decodes UTF-8, which is what a
        // browser does for every blob whose type does not say otherwise, and decoding a legacy
        // encoding needs a decoder table that is its own capability.
        startRead(this, blob, 'readAsText', function (bytes) {
            return new TextDecoder().decode(bytes);
        }, arguments.length);
    };

    FileReader.prototype.readAsArrayBuffer = function (blob) {
        startRead(this, blob, 'readAsArrayBuffer', function (bytes) {
            return bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength);
        }, arguments.length);
    };

    FileReader.prototype.readAsBinaryString = function (blob) {
        startRead(this, blob, 'readAsBinaryString', function (bytes) {
            var text = '';
            for (var i = 0; i < bytes.length; i++)
                text += String.fromCharCode(bytes[i]);
            return text;
        }, arguments.length);
    };

    FileReader.prototype.readAsDataURL = function (blob) {
        startRead(this, blob, 'readAsDataURL', function (bytes, source) {
            var binary = '';
            for (var i = 0; i < bytes.length; i++)
                binary += String.fromCharCode(bytes[i]);
            // A blob with no type reads as application/octet-stream, which is the specified default
            // and what a browser produces — measured, not the empty media type the blob reports.
            var type = source && source.type ? source.type : 'application/octet-stream';
            return 'data:' + type + ';base64,' + btoa(binary);
        }, arguments.length);
    };

    FileReader.prototype.abort = function () {
        var state = readerStateOf(this, 'abort');
        if (state.readyState !== 1) {
            state.result = null;
            return;
        }

        state.aborted = true;
        state.readyState = 2;
        state.result = null;
        fire(this, state, 'abort', 0, 0);
        fire(this, state, 'loadend', 0, 0);
    };

    FileReader.prototype.addEventListener = function (type, listener) {
        var state = readerStateOf(this, 'addEventListener');
        if (typeof listener !== 'function')
            return;
        var listeners = state.listeners[type] || (state.listeners[type] = []);
        if (listeners.indexOf(listener) < 0)
            listeners.push(listener);
    };

    FileReader.prototype.removeEventListener = function (type, listener) {
        var state = readerStateOf(this, 'removeEventListener');
        var listeners = state.listeners[type];
        if (!listeners)
            return;
        var index = listeners.indexOf(listener);
        if (index >= 0)
            listeners.splice(index, 1);
    };

    FileReader.prototype.dispatchEvent = function (event) {
        var state = readerStateOf(this, 'dispatchEvent');
        if (!event || !event.type)
            return true;
        fire(this, state, event.type, event.loaded || 0, event.total || 0);
        return true;
    };

    ['readyState', 'result', 'error'].forEach(function (name) {
        Object.defineProperty(FileReader.prototype, name, {
            get: function () { return readerStateOf(this, name)[name]; },
            enumerable: true, configurable: true,
        });
    });

    ['onloadstart', 'onprogress', 'onload', 'onabort', 'onerror', 'onloadend'].forEach(function (name) {
        Object.defineProperty(FileReader.prototype, name, {
            value: null, writable: true, enumerable: true, configurable: true,
        });
    });

    FileReader.prototype.EMPTY = 0;
    FileReader.prototype.LOADING = 1;
    FileReader.prototype.DONE = 2;

    globalThis.FileReader = FileReader;
})();
