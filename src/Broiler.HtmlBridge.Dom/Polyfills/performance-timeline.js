// The Performance Timeline (Level 2), User Timing (Level 3) and Resource Timing: the entry interfaces,
// the marks and measures a page records, the fetches it made, performance.getEntries* over them and
// PerformanceObserver. Every message, conversion and ordering below is Chromium's, measured.
//
// __broilerPerformanceTimeline is the host hook, captured into this closure and deleted from the global:
//   performance        the window's performance object, which the methods are installed on;
//   documentKey()      a number naming the document whose script is running -- every document shares
//                      this one global, so each keeps its own entries and observers under its key;
//   topDocumentKey()   the page's own;
//   navigationEntry()  that document's PerformanceNavigationTiming entry;
//   queueTask(fn)      queues fn as a task on the event loop;
//   bind(fn)           fn, made to run in the browsing context whose script is running;
//   clone(value, pre)  the structured clone of value, or a DataCloneError whose message starts with pre;
//   takeResources()    the fetches that have finished since it was last asked, each the values of its
//                      resource entry and the key of the document it belongs to;
// and the script sets three functions on it: loadEventEnded, which the host calls when the page's load
// event has ended -- when Chromium hands the page's navigation entry to the observers waiting for one --
// resourcesArrived, which it calls in a task once fetches have finished, and longTaskEnded, which it
// calls with a long task's start, duration and the documents it is reported to.
(function () {
    'use strict';

    var host = globalThis.__broilerPerformanceTimeline;
    delete globalThis.__broilerPerformanceTimeline;

    var performance = host.performance;
    var now = performance.now;
    var defineProperty = Object.defineProperty;
    var EventConstructor = globalThis.Event;

    // The entry types this timeline produces, which PerformanceObserver.supportedEntryTypes lists and
    // observe() accepts. Chromium's list is longer; a type no entry is ever made of is not claimed.
    var supportedTypes = ['longtask', 'mark', 'measure', 'navigation', 'resource'];
    var supportedEntryTypes = Object.freeze(supportedTypes.slice());

    // PerformanceTiming's attribute names: a mark may not take one, and a measure reads one as the
    // moment the navigation entry names.
    var timingNames = ['navigationStart', 'unloadEventStart', 'unloadEventEnd', 'redirectStart', 'redirectEnd',
        'fetchStart', 'domainLookupStart', 'domainLookupEnd', 'connectStart', 'connectEnd',
        'secureConnectionStart', 'requestStart', 'responseStart', 'responseEnd', 'domLoading',
        'domInteractive', 'domContentLoadedEventStart', 'domContentLoadedEventEnd', 'domComplete',
        'loadEventStart', 'loadEventEnd'];

    // ---------------------------------------------------------------- WebIDL

    function domException(message, name) { return new DOMException(message, name); }

    function failedToExecute(operation, interfaceName) { return "Failed to execute '" + operation + "' on '" + interfaceName + "': "; }

    function failedToConstruct(interfaceName) { return "Failed to construct '" + interfaceName + "': "; }

    function illegalInvocation() { return new TypeError('Illegal invocation'); }

    function requireArguments(count, required, prefix) {
        if (count < required)
            throw new TypeError(prefix + required + ' argument required, but only ' + count + ' present.');
    }

    function toDOMString(value, prefix) {
        if (typeof value === 'symbol')
            throw new TypeError(prefix + 'Cannot convert a Symbol value to a string');
        return String(value);
    }

    // A dictionary: undefined and null are an empty one, any other value that is not an object is refused.
    function toDictionary(value, dictionaryName, prefix) {
        if (value === undefined || value === null)
            return null;
        if (typeof value !== 'object' && typeof value !== 'function')
            throw new TypeError(prefix + "The provided value is not of type '" + dictionaryName + "'.");
        return value;
    }

    // A double member of a dictionary.
    function toFiniteDouble(value, member, dictionaryName, prefix) {
        var read = prefix + "Failed to read the '" + member + "' property from '" + dictionaryName + "': ";
        if (typeof value === 'symbol')
            throw new TypeError(read + 'Cannot convert a Symbol value to a number');
        var number = +value;
        if (!isFinite(number))
            throw new TypeError(read + 'The provided double value is non-finite.');
        return number;
    }

    // Members as WebIDL makes them: enumerable and configurable, attributes without a setter, operations
    // writable and not constructors (method shorthand).
    function defineGetter(target, name, get) {
        defineProperty(target, name, { get: get, enumerable: true, configurable: true });
    }

    function defineOperations(target, operations) {
        Object.keys(operations).forEach(function (name) {
            defineProperty(target, name, { value: operations[name], writable: true, enumerable: true, configurable: true });
        });
    }

    function defineInterface(interfaceName, constructor, parent) {
        if (parent) {
            Object.setPrototypeOf(constructor, parent);
            Object.setPrototypeOf(constructor.prototype, parent.prototype);
        }
        defineProperty(constructor, 'name', { value: interfaceName, configurable: true });
        defineProperty(constructor.prototype, Symbol.toStringTag, { value: interfaceName, configurable: true });
        defineProperty(globalThis, interfaceName, { value: constructor, writable: true, enumerable: false, configurable: true });
        return constructor;
    }

    function illegalConstructor(interfaceName) {
        return function () { throw new TypeError(failedToConstruct(interfaceName) + 'Illegal constructor'); };
    }

    function constructOnly(target, interfaceName) {
        if (!target)
            throw new TypeError(failedToConstruct(interfaceName) +
                "Please use the 'new' operator, this DOM object constructor cannot be called as a function.");
    }

    // ---------------------------------------------------------------- entries

    // An entry's attributes are internal slots its getters read, kept here; the host's navigation entry
    // has none and answers with members of its own.
    var entrySlots = new WeakMap();
    var sequence = 0;

    function slotsOf(entry) {
        var slots = entrySlots.get(entry);
        if (!slots)
            throw illegalInvocation();
        return slots;
    }

    function newEntry(prototype, slots) {
        var entry = Object.create(prototype);
        slots.sequence = ++sequence;
        entrySlots.set(entry, slots);
        return entry;
    }

    var PerformanceEntry = defineInterface('PerformanceEntry', illegalConstructor('PerformanceEntry'));
    defineGetter(PerformanceEntry.prototype, 'name', function () { return slotsOf(this).name; });
    defineGetter(PerformanceEntry.prototype, 'entryType', function () { return slotsOf(this).entryType; });
    defineGetter(PerformanceEntry.prototype, 'startTime', function () { return slotsOf(this).startTime; });
    defineGetter(PerformanceEntry.prototype, 'duration', function () { return slotsOf(this).duration; });
    defineOperations(PerformanceEntry.prototype, {
        toJSON() {
            return { name: this.name, entryType: this.entryType, startTime: this.startTime, duration: this.duration };
        }
    });

    // A mark is constructible: `new PerformanceMark(name, options)` makes one without recording it.
    var PerformanceMark = defineInterface('PerformanceMark', function PerformanceMark(markName) {
        var prefix = failedToConstruct('PerformanceMark');
        constructOnly(new.target, 'PerformanceMark');
        requireArguments(arguments.length, 1, prefix);
        var slots = markSlots(markName, arguments[1], prefix);
        slots.sequence = ++sequence;
        entrySlots.set(this, slots);
    }, PerformanceEntry);

    var PerformanceMeasure = defineInterface('PerformanceMeasure', illegalConstructor('PerformanceMeasure'), PerformanceEntry);

    function detailGetter(entryType) {
        return function () {
            var slots = slotsOf(this);
            if (slots.entryType !== entryType)
                throw illegalInvocation();
            return slots.detail;
        };
    }

    defineGetter(PerformanceMark.prototype, 'detail', detailGetter('mark'));
    defineGetter(PerformanceMeasure.prototype, 'detail', detailGetter('measure'));

    // A resource entry's attributes, in the order its toJSON lists them after the PerformanceEntry four.
    var resourceAttributes = ['initiatorType', 'deliveryType', 'nextHopProtocol', 'renderBlockingStatus', 'contentType',
        'contentEncoding', 'workerStart', 'workerRouterEvaluationStart', 'workerCacheLookupStart', 'workerMatchedSourceType',
        'workerFinalSourceType', 'redirectStart', 'redirectEnd', 'fetchStart', 'domainLookupStart', 'domainLookupEnd',
        'connectStart', 'secureConnectionStart', 'connectEnd', 'requestStart', 'responseStart', 'firstInterimResponseStart',
        'finalResponseHeadersStart', 'responseEnd', 'transferSize', 'encodedBodySize', 'decodedBodySize', 'responseStatus',
        'serverTiming'];
    var stringAttributes = ['initiatorType', 'deliveryType', 'nextHopProtocol', 'renderBlockingStatus', 'contentType',
        'contentEncoding', 'workerMatchedSourceType', 'workerFinalSourceType'];

    // What the host's navigation entry does not carry itself, which it answers as Chromium's does for a
    // document fetched without a service worker, interim responses or server timing.
    var navigationDefaults = {
        deliveryType: '', renderBlockingStatus: 'non-blocking', contentType: 'text/html', contentEncoding: '',
        workerRouterEvaluationStart: 0, workerCacheLookupStart: 0, workerMatchedSourceType: '', workerFinalSourceType: '',
        firstInterimResponseStart: 0, responseStatus: 0
    };
    var navigationEntries = new WeakSet();

    function ownValue(object, name) {
        var own = Object.getOwnPropertyDescriptor(object, name);
        return own ? ('value' in own ? own.value : own.get.call(object)) : undefined;
    }

    function navigationAttribute(entry, name) {
        var own = Object.getOwnPropertyDescriptor(entry, name);
        if (own)
            return ownValue(entry, name);
        if (name === 'finalResponseHeadersStart')
            return ownValue(entry, 'responseStart');
        if (name === 'serverTiming')
            return Object.freeze([]);
        return navigationDefaults[name];
    }

    // The navigation entry is the host's, with its members its own; these give it its interfaces.
    var PerformanceResourceTiming = defineInterface('PerformanceResourceTiming', illegalConstructor('PerformanceResourceTiming'), PerformanceEntry);
    var PerformanceNavigationTiming = defineInterface('PerformanceNavigationTiming', illegalConstructor('PerformanceNavigationTiming'), PerformanceResourceTiming);

    resourceAttributes.forEach(function (name) {
        defineGetter(PerformanceResourceTiming.prototype, name, function () {
            var slots = entrySlots.get(this);
            if (slots && slots.entryType === 'resource')
                // A new frozen array of the same PerformanceServerTiming objects at every read.
                return name === 'serverTiming' ? Object.freeze(slots.serverTiming.slice()) : slots[name];
            if (!slots && navigationEntries.has(this))
                return navigationAttribute(this, name);
            throw illegalInvocation();
        });
    });
    defineOperations(PerformanceResourceTiming.prototype, {
        toJSON() {
            var json = PerformanceEntry.prototype.toJSON.call(this);
            var entry = this;
            resourceAttributes.forEach(function (name) { json[name] = entry[name]; });
            return json;
        }
    });

    // A Server-Timing metric, as Chromium parses the header: a name, then parameters of which the
    // first dur and the first desc are kept.
    var serverTimingSlots = new WeakMap();
    var PerformanceServerTiming = defineInterface('PerformanceServerTiming', illegalConstructor('PerformanceServerTiming'));
    ['name', 'duration', 'description'].forEach(function (name) {
        defineGetter(PerformanceServerTiming.prototype, name, function () {
            var slots = serverTimingSlots.get(this);
            if (!slots)
                throw illegalInvocation();
            return slots[name];
        });
    });
    defineOperations(PerformanceServerTiming.prototype, {
        toJSON() {
            var slots = serverTimingSlots.get(this);
            if (!slots)
                throw illegalInvocation();
            return { name: slots.name, duration: slots.duration, description: slots.description };
        }
    });

    var tokenCharacter = /[!#$%&'*+\-.^_`|~0-9A-Za-z]/;

    function parseServerTiming(header) {
        var metrics = [], at = 0;
        function skipSpace() { while (at < header.length && (header[at] === ' ' || header[at] === '\t')) at++; }
        function token() {
            var start = at;
            while (at < header.length && tokenCharacter.test(header[at])) at++;
            return header.slice(start, at);
        }
        function tokenOrQuotedString() {
            if (header[at] !== '"')
                return token();
            var value = '';
            for (at++; at < header.length && header[at] !== '"'; at++) {
                if (header[at] === '\\' && at + 1 < header.length)
                    at++;
                value += header[at];
            }
            at++;
            return value;
        }
        // Skips what a metric or a parameter does not consume, up to the next ',' or ';'.
        function skipToDelimiter() {
            while (at < header.length && header[at] !== ',' && header[at] !== ';') {
                if (header[at] === '"')
                    tokenOrQuotedString();
                else
                    at++;
            }
        }
        while (at < header.length) {
            skipSpace();
            var name = token();
            if (!name)
                break;
            var metric = { name: name, duration: 0, description: '' }, hasDuration = false, hasDescription = false;
            skipToDelimiter();
            while (header[at] === ';') {
                at++;
                skipSpace();
                var parameter = token().toLowerCase();
                if (!parameter)
                    break;
                skipSpace();
                var value = '';
                if (header[at] === '=') {
                    at++;
                    skipSpace();
                    value = tokenOrQuotedString();
                    skipToDelimiter();
                }
                if (parameter === 'dur' && !hasDuration) {
                    var duration = Number(value);
                    metric.duration = value !== '' && isFinite(duration) ? duration : 0;
                    hasDuration = true;
                } else if (parameter === 'desc' && !hasDescription) {
                    metric.description = value;
                    hasDescription = true;
                }
            }
            var serverTiming = Object.create(PerformanceServerTiming.prototype);
            serverTimingSlots.set(serverTiming, metric);
            metrics.push(serverTiming);
            if (header[at] !== ',')
                break;
            at++;
        }
        return metrics;
    }

    function nameOf(entry) { var slots = entrySlots.get(entry); return slots ? slots.name : entry.name; }
    function typeOf(entry) { var slots = entrySlots.get(entry); return slots ? slots.entryType : entry.entryType; }
    function startOf(entry) { var slots = entrySlots.get(entry); return slots ? slots.startTime : entry.startTime; }
    function sequenceOf(entry) { var slots = entrySlots.get(entry); return slots ? slots.sequence : 0; }

    // In order of their start, and of their recording where two start together -- a stable order
    // whatever the engine's sort is.
    function chronological(entries) {
        return entries.slice().sort(function (a, b) { return startOf(a) - startOf(b) || sequenceOf(a) - sequenceOf(b); });
    }

    function filtered(entries, name, type) {
        return chronological(entries.filter(function (entry) {
            return (name === null || nameOf(entry) === name) && (type === null || typeOf(entry) === type);
        }));
    }

    // A long task, and what it is attributed to: one TaskAttributionTiming, naming the frame element
    // the task came from, or the window itself.
    var PerformanceLongTaskTiming = defineInterface('PerformanceLongTaskTiming', illegalConstructor('PerformanceLongTaskTiming'), PerformanceEntry);
    var TaskAttributionTiming = defineInterface('TaskAttributionTiming', illegalConstructor('TaskAttributionTiming'), PerformanceEntry);
    var containerAttributes = ['containerType', 'containerSrc', 'containerId', 'containerName'];

    defineGetter(PerformanceLongTaskTiming.prototype, 'attribution', function () {
        var slots = slotsOf(this);
        if (slots.entryType !== 'longtask')
            throw illegalInvocation();
        return Object.freeze(slots.attribution.slice());
    });
    defineOperations(PerformanceLongTaskTiming.prototype, {
        toJSON() {
            var json = PerformanceEntry.prototype.toJSON.call(this);
            json.attribution = this.attribution;
            return json;
        }
    });

    containerAttributes.forEach(function (name) {
        defineGetter(TaskAttributionTiming.prototype, name, function () {
            var slots = slotsOf(this);
            if (slots.entryType !== 'taskattribution')
                throw illegalInvocation();
            return slots[name];
        });
    });
    defineOperations(TaskAttributionTiming.prototype, {
        toJSON() {
            var json = PerformanceEntry.prototype.toJSON.call(this);
            var entry = this;
            containerAttributes.forEach(function (name) { json[name] = entry[name]; });
            return json;
        }
    });

    // ---------------------------------------------------------------- the documents' timelines

    var timelines = new Map();

    function timelineOf(key) {
        var timeline = timelines.get(key);
        if (!timeline) {
            timeline = {
                entries: [], observers: [], taskQueued: false, navigation: undefined,
                // Resource Timing's buffer, the one an entry waits in while it is full, its size limit
                // and the buffer-full event's state.
                resources: [], secondary: [], resourceLimit: 250, bufferFullPending: false, bufferFullHandler: null,
                // The long tasks a buffered observer is handed, which the timeline's getters leave out.
                longtasks: []
            };
            timelines.set(key, timeline);
        }
        return timeline;
    }

    function currentTimeline() { return timelineOf(host.documentKey()); }

    function navigationOf(timeline) {
        if (timeline.navigation === undefined) {
            var entry = host.navigationEntry();
            if (entry && Object.getPrototypeOf(entry) === Object.prototype)
                Object.setPrototypeOf(entry, PerformanceNavigationTiming.prototype);
            if (entry)
                navigationEntries.add(entry);
            timeline.navigation = entry || null;
        }
        return timeline.navigation;
    }

    // Everything the document's buffer holds: its navigation entry, then what its script recorded and
    // the fetches it made -- every one that has finished by now, delivered or not.
    function bufferOf(timeline) {
        drainResources();
        var navigation = navigationOf(timeline);
        var recorded = timeline.entries.concat(timeline.resources);
        return navigation ? [navigation].concat(recorded) : recorded;
    }

    // Hands an entry to the document's observers of its type; a mark or a measure is also recorded.
    function queueEntry(timeline, entry, record) {
        if (record)
            timeline.entries.push(entry);
        var type = typeOf(entry);
        var queued = false;
        timeline.observers.forEach(function (observer) {
            var state = observerState.get(observer);
            if (state.types.indexOf(type) >= 0) {
                state.buffer.push(entry);
                queued = true;
            }
        });
        if (queued)
            queueObserverTask(timeline);
    }

    // One task delivers to every observer of the document with entries waiting, in the order they began
    // observing. A callback that throws does not keep the others from theirs; the first throw is
    // reported once they have run.
    function queueObserverTask(timeline) {
        if (timeline.taskQueued)
            return;
        timeline.taskQueued = true;
        host.queueTask(function () {
            timeline.taskQueued = false;
            var failure = null;
            timeline.observers.slice().forEach(function (observer) {
                var state = observerState.get(observer);
                if (state.buffer.length === 0)
                    return;
                var list = Object.create(PerformanceObserverEntryList.prototype);
                listEntries.set(list, state.buffer);
                state.buffer = [];
                try {
                    state.callback.call(observer, list, observer, { droppedEntriesCount: 0 });
                } catch (error) {
                    if (failure === null)
                        failure = { error: error };
                }
            });
            if (failure !== null)
                throw failure.error;
        });
    }

    // ---------------------------------------------------------------- User Timing

    function isTimingName(name) { return timingNames.indexOf(name) >= 0; }

    function cloneDetail(detail, prefix) {
        return detail === undefined || detail === null ? null : host.clone(detail, prefix);
    }

    // What a mark is made of, for mark() and new PerformanceMark() alike.
    function markSlots(markName, markOptions, prefix) {
        var name = toDOMString(markName, prefix);
        var options = toDictionary(markOptions, 'PerformanceMarkOptions', prefix);
        var detail = options === null ? undefined : options.detail;
        var startTime = options === null ? undefined : options.startTime;
        if (startTime !== undefined)
            startTime = toFiniteDouble(startTime, 'startTime', 'PerformanceMarkOptions', prefix);
        if (isTimingName(name))
            throw domException(prefix + "'" + name + "' is part of the PerformanceTiming interface, and cannot be used as a mark name.", 'SyntaxError');
        if (startTime !== undefined && startTime < 0)
            throw new TypeError(prefix + "'" + name + "' cannot have a negative start time.");
        return {
            name: name,
            entryType: 'mark',
            startTime: startTime === undefined ? now.call(performance) : startTime,
            duration: 0,
            detail: cloneDetail(detail, prefix)
        };
    }

    // A mark's name or a timestamp, as a time on the document's timeline. A PerformanceTiming name is
    // the moment the navigation entry gives it, which must have happened.
    function toTimestamp(timeline, mark, measureName, prefix) {
        if (typeof mark === 'number') {
            if (mark < 0)
                throw new TypeError(prefix + "'" + measureName + "' cannot have a negative time stamp.");
            return mark;
        }
        if (isTimingName(mark)) {
            if (mark === 'navigationStart')
                return 0;
            var navigation = navigationOf(timeline);
            var value = navigation ? +navigation[mark === 'domLoading' ? 'responseEnd' : mark] : 0;
            if (!(value > 0))
                throw domException(prefix + "'" + mark + "' is empty: either the event hasn't happened yet, or it would provide cross-origin timing information.", 'InvalidAccessError');
            return value;
        }
        for (var i = timeline.entries.length - 1; i >= 0; i--) {
            var slots = entrySlots.get(timeline.entries[i]);
            if (slots.entryType === 'mark' && slots.name === mark)
                return slots.startTime;
        }
        throw domException(prefix + "The mark '" + mark + "' does not exist.", 'SyntaxError');
    }

    // A (DOMString or DOMHighResTimeStamp) member of PerformanceMeasureOptions: a number is a time, and
    // anything else the name of a mark.
    function timeOrMarkName(value, member, prefix) {
        if (value === undefined)
            return undefined;
        return typeof value === 'number'
            ? toFiniteDouble(value, member, 'PerformanceMeasureOptions', prefix)
            : toDOMString(value, prefix);
    }

    // The second argument of measure(): (DOMString or PerformanceMeasureOptions). An object is the
    // dictionary, as are undefined and null; anything else is a mark's name. A dictionary with none of
    // its members is no dictionary at all.
    function measureOptions(value, prefix) {
        if (value === undefined || value === null)
            return { options: null, startMark: undefined };
        if (typeof value !== 'object' && typeof value !== 'function')
            return { options: null, startMark: toDOMString(value, prefix) };
        var options = { detail: value.detail, duration: value.duration, end: value.end, start: value.start };
        if (options.duration !== undefined)
            options.duration = toFiniteDouble(options.duration, 'duration', 'PerformanceMeasureOptions', prefix);
        options.end = timeOrMarkName(options.end, 'end', prefix);
        options.start = timeOrMarkName(options.start, 'start', prefix);
        var empty = options.detail === undefined && options.duration === undefined &&
            options.end === undefined && options.start === undefined;
        return { options: empty ? null : options, startMark: undefined };
    }

    function clearEntries(entryType, name, prefix) {
        var timeline = currentTimeline();
        var only = name === undefined ? null : toDOMString(name, prefix);
        timeline.entries = timeline.entries.filter(function (entry) {
            var slots = entrySlots.get(entry);
            return slots.entryType !== entryType || only !== null && slots.name !== only;
        });
    }

    defineOperations(performance, {
        mark(markName) {
            var prefix = failedToExecute('mark', 'Performance');
            requireArguments(arguments.length, 1, prefix);
            var entry = newEntry(PerformanceMark.prototype, markSlots(markName, arguments[1], prefix));
            queueEntry(currentTimeline(), entry, true);
            return entry;
        },

        measure(measureName) {
            var prefix = failedToExecute('measure', 'Performance');
            requireArguments(arguments.length, 1, prefix);
            var name = toDOMString(measureName, prefix);
            var second = measureOptions(arguments[1], prefix);
            var options = second.options;
            var endMark = arguments[2] === undefined ? undefined : toDOMString(arguments[2], prefix);
            var timeline = currentTimeline();

            if (options !== null) {
                if (endMark !== undefined)
                    throw new TypeError(prefix + 'If a non-empty PerformanceMeasureOptions object was passed, |end_mark| must not be passed.');
                if (options.start === undefined && options.end === undefined)
                    throw new TypeError(prefix + "If a non-empty PerformanceMeasureOptions object was passed, at least one of its 'start' or 'end' properties must be present.");
                if (options.start !== undefined && options.duration !== undefined && options.end !== undefined)
                    throw new TypeError(prefix + "If a non-empty PerformanceMeasureOptions object was passed, it must not have all of its 'start', 'duration', and 'end' properties defined");
            }

            var end;
            if (endMark !== undefined)
                end = toTimestamp(timeline, endMark, name, prefix);
            else if (options !== null && options.end !== undefined)
                end = toTimestamp(timeline, options.end, name, prefix);
            else if (options !== null && options.start !== undefined && options.duration !== undefined)
                end = toTimestamp(timeline, options.start, name, prefix) + options.duration;
            else
                end = now.call(performance);

            var start;
            if (options !== null && options.start !== undefined)
                start = toTimestamp(timeline, options.start, name, prefix);
            else if (options !== null && options.duration !== undefined && options.end !== undefined)
                start = end - options.duration;
            else if (second.startMark !== undefined)
                start = toTimestamp(timeline, second.startMark, name, prefix);
            else
                start = 0;

            var entry = newEntry(PerformanceMeasure.prototype, {
                name: name,
                entryType: 'measure',
                startTime: start,
                duration: end - start,
                detail: cloneDetail(options === null ? undefined : options.detail, prefix)
            });
            queueEntry(timeline, entry, true);
            return entry;
        },

        clearMarks() { clearEntries('mark', arguments[0], failedToExecute('clearMarks', 'Performance')); },

        clearMeasures() { clearEntries('measure', arguments[0], failedToExecute('clearMeasures', 'Performance')); },

        getEntries() { return chronological(bufferOf(currentTimeline())); },

        getEntriesByType(type) {
            var prefix = failedToExecute('getEntriesByType', 'Performance');
            requireArguments(arguments.length, 1, prefix);
            return filtered(bufferOf(currentTimeline()), null, toDOMString(type, prefix));
        },

        getEntriesByName(name) {
            var prefix = failedToExecute('getEntriesByName', 'Performance');
            requireArguments(arguments.length, 1, prefix);
            var type = arguments[1];
            return filtered(bufferOf(currentTimeline()), toDOMString(name, prefix),
                type === undefined ? null : toDOMString(type, prefix));
        }
    });

    // ---------------------------------------------------------------- PerformanceObserverEntryList

    var listEntries = new WeakMap();

    function entriesOfList(list) {
        var entries = listEntries.get(list);
        if (!entries)
            throw illegalInvocation();
        return entries;
    }

    var PerformanceObserverEntryList = defineInterface('PerformanceObserverEntryList', illegalConstructor('PerformanceObserverEntryList'));
    defineOperations(PerformanceObserverEntryList.prototype, {
        getEntries() { return chronological(entriesOfList(this)); },

        getEntriesByType(type) {
            var entries = entriesOfList(this);
            var prefix = failedToExecute('getEntriesByType', 'PerformanceObserverEntryList');
            requireArguments(arguments.length, 1, prefix);
            return filtered(entries, null, toDOMString(type, prefix));
        },

        getEntriesByName(name) {
            var entries = entriesOfList(this);
            var prefix = failedToExecute('getEntriesByName', 'PerformanceObserverEntryList');
            requireArguments(arguments.length, 1, prefix);
            var type = arguments[1];
            return filtered(entries, toDOMString(name, prefix), type === undefined ? null : toDOMString(type, prefix));
        }
    });

    // ---------------------------------------------------------------- PerformanceObserver

    var observerState = new WeakMap();

    function stateOf(observer) {
        var state = observerState.get(observer);
        if (!state)
            throw illegalInvocation();
        return state;
    }

    // An observer belongs to the document whose script made it, and its callback runs in that
    // document's browsing context with the observer as `this`.
    var PerformanceObserver = defineInterface('PerformanceObserver', function PerformanceObserver(callback) {
        var prefix = failedToConstruct('PerformanceObserver');
        constructOnly(new.target, 'PerformanceObserver');
        requireArguments(arguments.length, 1, prefix);
        if (typeof callback !== 'function')
            throw new TypeError(prefix + "parameter 1 is not of type 'Function'.");
        observerState.set(this, {
            callback: host.bind(callback),
            timeline: currentTimeline(),
            mode: null,
            types: [],
            buffer: []
        });
    });
    defineProperty(PerformanceObserver, 'supportedEntryTypes', {
        get: Object.getOwnPropertyDescriptor({ get supportedEntryTypes() { return supportedEntryTypes; } }, 'supportedEntryTypes').get,
        enumerable: true,
        configurable: true
    });

    function observerInit(value, prefix) {
        var init = toDictionary(value, 'PerformanceObserverInit', prefix) || {};
        var buffered = !!init.buffered;
        var entryTypes = init.entryTypes;
        var type = init.type;
        if (entryTypes !== undefined) {
            if (entryTypes === null || typeof entryTypes !== 'object' && typeof entryTypes !== 'function' ||
                typeof entryTypes[Symbol.iterator] !== 'function')
                throw new TypeError(prefix + "Failed to read the 'entryTypes' property from 'PerformanceObserverInit': " +
                    'The provided value cannot be converted to a sequence.');
            entryTypes = Array.from(entryTypes, function (value) { return toDOMString(value, prefix); });
        }
        if (type !== undefined)
            type = toDOMString(type, prefix);
        return { buffered: buffered, entryTypes: entryTypes, type: type };
    }

    defineOperations(PerformanceObserver.prototype, {
        observe() {
            var state = stateOf(this);
            var prefix = failedToExecute('observe', 'PerformanceObserver');
            var init = observerInit(arguments[0], prefix);

            if (init.entryTypes === undefined && init.type === undefined)
                throw new TypeError(prefix + 'An observe() call must include either entryTypes or type arguments.');
            if (init.entryTypes !== undefined && init.type !== undefined)
                throw new TypeError(prefix + 'An observe() call must not include both entryTypes and type arguments.');
            if (state.mode === 'multiple' && init.type !== undefined)
                throw domException(prefix + 'This observer has performed observe({entryTypes:...}, therefore it cannot perform observe({type:...})', 'InvalidModificationError');
            if (state.mode === 'single' && init.entryTypes !== undefined)
                throw domException(prefix + 'This PerformanceObserver has performed observe({type:...}, therefore it cannot perform observe({entryTypes:...})', 'InvalidModificationError');

            var timeline = state.timeline;
            if (init.entryTypes !== undefined) {
                // The types the timeline knows replace the observer's earlier ones; the rest are ignored,
                // an observer left with none is not registered, and `buffered` does not apply.
                var known = init.entryTypes.filter(function (value, index, all) {
                    return supportedTypes.indexOf(value) >= 0 && all.indexOf(value) === index;
                });
                if (known.length === 0)
                    return;
                state.mode = 'multiple';
                state.types = known;
            } else {
                if (supportedTypes.indexOf(init.type) < 0)
                    return;
                state.mode = 'single';
                if (state.types.indexOf(init.type) < 0)
                    state.types.push(init.type);
                // What the document recorded of the type, each time it is asked for -- twice, if twice.
                if (init.buffered) {
                    var earlier = init.type === 'longtask' ? timeline.longtasks.slice() : filtered(bufferOf(timeline), null, init.type);
                    earlier.forEach(function (entry) { state.buffer.push(entry); });
                    if (earlier.length > 0)
                        queueObserverTask(timeline);
                }
            }
            if (timeline.observers.indexOf(this) < 0)
                timeline.observers.push(this);
        },

        disconnect() {
            var state = stateOf(this);
            var observers = state.timeline.observers;
            var index = observers.indexOf(this);
            if (index >= 0)
                observers.splice(index, 1);
            state.buffer = [];
            state.types = [];
            state.mode = null;
        },

        takeRecords() {
            var state = stateOf(this);
            drainResources();
            var records = state.buffer;
            state.buffer = [];
            return records;
        }
    });

    // ---------------------------------------------------------------- Resource Timing

    // A finished fetch's entry goes to the observers of its document, and into the document's buffer
    // while that has room. A full buffer keeps it aside and fires resourcetimingbufferfull in a task;
    // what the handlers make room for is moved in, and what they do not is dropped.
    function drainResources() {
        var arrived = host.takeResources();
        for (var i = 0; i < arrived.length; i++)
            addResource(timelineOf(arrived[i].key), newResourceEntry(arrived[i]));
    }

    function newResourceEntry(data) {
        var slots = {
            name: data.name,
            entryType: 'resource',
            startTime: data.startTime,
            duration: data.responseEnd - data.startTime,
            serverTiming: data.serverTiming ? parseServerTiming(data.serverTiming) : []
        };
        // What the host leaves out is what the fetch did not reveal: the empty string, or 0.
        resourceAttributes.forEach(function (name) {
            if (name !== 'serverTiming')
                slots[name] = data[name] !== undefined ? data[name] : stringAttributes.indexOf(name) >= 0 ? '' : 0;
        });
        return newEntry(PerformanceResourceTiming.prototype, slots);
    }

    function addResource(timeline, entry) {
        queueEntry(timeline, entry, false);
        if (timeline.resources.length < timeline.resourceLimit) {
            timeline.resources.push(entry);
            return;
        }
        timeline.secondary.push(entry);
        if (!timeline.bufferFullPending) {
            timeline.bufferFullPending = true;
            host.queueTask(function () { fireBufferFull(timeline); });
        }
    }

    function fireBufferFull(timeline) {
        while (timeline.secondary.length > 0) {
            var before = timeline.secondary.length;
            if (timeline.resources.length >= timeline.resourceLimit)
                dispatchBufferFull(timeline);
            while (timeline.secondary.length > 0 && timeline.resources.length < timeline.resourceLimit)
                timeline.resources.push(timeline.secondary.shift());
            if (timeline.secondary.length >= before) {
                timeline.secondary = [];
                break;
            }
        }
        timeline.bufferFullPending = false;
    }

    // resourcetimingbufferfull, an Event the user agent makes, at performance.
    function dispatchBufferFull(timeline) {
        var event = Object.create(EventConstructor.prototype);
        [['type', 'resourcetimingbufferfull'], ['bubbles', false], ['cancelable', false], ['composed', false],
            ['defaultPrevented', false], ['timeStamp', now.call(performance)]].forEach(function (member) {
            defineProperty(event, member[0], { value: member[1], writable: true, enumerable: true, configurable: true });
        });
        defineProperty(event, 'isTrusted', { get: function () { return true; }, enumerable: true });
        dispatchTo(timeline, event);
    }

    // ---------------------------------------------------------------- performance as an EventTarget

    // Every document shares the performance object, so each keeps its own listeners, as it keeps its own
    // entries, and they run in its browsing context with performance as `this`. Its event handler is one
    // of them, from the first time it is set, as HTML has it.
    function listenersOf(timeline) { return timeline.listeners || (timeline.listeners = []); }

    function listenerOptions(options) {
        if (options !== null && (typeof options === 'object' || typeof options === 'function'))
            return { capture: !!options.capture, once: !!options.once, signal: options.signal };
        return { capture: !!options, once: false, signal: undefined };
    }

    function removeListener(listeners, listener) {
        var index = listeners.indexOf(listener);
        if (index >= 0)
            listeners.splice(index, 1);
        listener.removed = true;
    }

    // An own data property of the event, whatever the event made it: target, currentTarget, eventPhase.
    function setEventMember(event, name, value) {
        try {
            defineProperty(event, name, { value: value, writable: true, enumerable: true, configurable: true });
        } catch (error) {
            // A member the event fixed for itself keeps its value.
        }
    }

    function dispatchTo(timeline, event) {
        setEventMember(event, 'target', performance);
        setEventMember(event, 'srcElement', performance);
        setEventMember(event, 'currentTarget', performance);
        setEventMember(event, 'eventPhase', 2);
        var listeners = listenersOf(timeline);
        listeners.slice().forEach(function (listener) {
            if (listener.removed || listener.type !== event.type)
                return;
            if (listener.once)
                removeListener(listeners, listener);
            try {
                listener.bound.call(performance, event);
            } catch (error) {
                host.queueTask(function () { throw error; });
            }
        });
        setEventMember(event, 'currentTarget', null);
        setEventMember(event, 'eventPhase', 0);
        return !event.defaultPrevented;
    }

    defineOperations(performance, {
        addEventListener(type, callback) {
            var prefix = failedToExecute('addEventListener', 'EventTarget');
            requireArguments(arguments.length, 2, prefix);
            type = toDOMString(type, prefix);
            var options = listenerOptions(arguments[2]);
            if (callback === null || callback === undefined)
                return;
            if (typeof callback !== 'function' && typeof callback !== 'object')
                throw new TypeError(prefix + "parameter 2 is not of type 'Object'.");
            if (options.signal && options.signal.aborted)
                return;
            var listeners = listenersOf(currentTimeline());
            for (var i = 0; i < listeners.length; i++) {
                if (listeners[i].type === type && listeners[i].callback === callback && listeners[i].capture === options.capture)
                    return;
            }
            var listener = {
                type: type,
                callback: callback,
                capture: options.capture,
                once: options.once,
                removed: false,
                bound: host.bind(typeof callback === 'function' ? callback : function (event) {
                    var handleEvent = callback.handleEvent;
                    if (typeof handleEvent !== 'function')
                        throw new TypeError("'handleEvent' property of event listener should be callable.");
                    return handleEvent.call(callback, event);
                })
            };
            listeners.push(listener);
            if (options.signal && typeof options.signal.addEventListener === 'function')
                options.signal.addEventListener('abort', function () { removeListener(listeners, listener); });
        },

        removeEventListener(type, callback) {
            var prefix = failedToExecute('removeEventListener', 'EventTarget');
            requireArguments(arguments.length, 2, prefix);
            type = toDOMString(type, prefix);
            var capture = listenerOptions(arguments[2]).capture;
            var listeners = listenersOf(currentTimeline());
            for (var i = 0; i < listeners.length; i++) {
                if (!listeners[i].handler && listeners[i].type === type && listeners[i].callback === callback && listeners[i].capture === capture) {
                    removeListener(listeners, listeners[i]);
                    return;
                }
            }
        },

        dispatchEvent(event) {
            var prefix = failedToExecute('dispatchEvent', 'EventTarget');
            requireArguments(arguments.length, 1, prefix);
            if (!(event instanceof EventConstructor))
                throw new TypeError(prefix + "parameter 1 is not of type 'Event'.");
            return dispatchTo(currentTimeline(), event);
        },

        clearResourceTimings() {
            drainResources();
            currentTimeline().resources = [];
        },

        setResourceTimingBufferSize(maxSize) {
            requireArguments(arguments.length, 1, failedToExecute('setResourceTimingBufferSize', 'Performance'));
            // An unsigned long. A smaller limit removes nothing the buffer already holds.
            currentTimeline().resourceLimit = maxSize >>> 0;
        }
    });

    // An event handler attribute: a value that is not an object is null.
    defineProperty(performance, 'onresourcetimingbufferfull', {
        get: Object.getOwnPropertyDescriptor({
            get onresourcetimingbufferfull() {
                return currentTimeline().bufferFullHandler;
            }
        }, 'onresourcetimingbufferfull').get,
        set: Object.getOwnPropertyDescriptor({
            set onresourcetimingbufferfull(value) {
                var timeline = currentTimeline();
                timeline.bufferFullHandler = typeof value === 'function' || (typeof value === 'object' && value !== null) ? value : null;
                if (timeline.bufferFullHandler && !timeline.handlerListener) {
                    timeline.handlerListener = {
                        type: 'resourcetimingbufferfull',
                        handler: true,
                        removed: false,
                        bound: host.bind(function (event) {
                            return typeof timeline.bufferFullHandler === 'function' ? timeline.bufferFullHandler.call(this, event) : undefined;
                        })
                    };
                    listenersOf(timeline).push(timeline.handlerListener);
                }
            }
        }, 'onresourcetimingbufferfull').set,
        enumerable: true,
        configurable: true
    });

    host.resourcesArrived = drainResources;

    // ---------------------------------------------------------------- Long Tasks

    var longTaskBufferSize = 200;

    // A long task's entry for each document it is reported to: the first 200 kept for a buffered
    // observer, every one handed to the document's observers.
    host.longTaskEnded = function (startTime, duration, reports) {
        for (var i = 0; i < reports.length; i++) {
            var report = reports[i];
            var timeline = timelineOf(report.key);
            var attribution = newEntry(TaskAttributionTiming.prototype, {
                name: 'unknown', entryType: 'taskattribution', startTime: 0, duration: 0,
                containerType: report.containerType, containerSrc: report.containerSrc,
                containerId: report.containerId, containerName: report.containerName
            });
            var entry = newEntry(PerformanceLongTaskTiming.prototype, {
                name: report.name, entryType: 'longtask', startTime: startTime, duration: duration, attribution: [attribution]
            });
            if (timeline.longtasks.length < longTaskBufferSize)
                timeline.longtasks.push(entry);
            queueEntry(timeline, entry, false);
        }
    };

    // ---------------------------------------------------------------- Navigation Timing Level 1

    // performance.timing and performance.navigation: the navigation entry's moments as integer
    // milliseconds since the epoch, and its type and redirects. A moment that is part of every fetch
    // reads from the navigation's start on; one that has not happened -- an event not yet fired, a
    // redirect or an unload there was none of -- reads 0.
    var timeOrigin = performance.timeOrigin;
    var fetchMoments = ['navigationStart', 'fetchStart', 'domainLookupStart', 'domainLookupEnd', 'connectStart', 'connectEnd',
        'requestStart', 'responseStart', 'responseEnd', 'domLoading'];

    function epochTime(timeline, name) {
        if (name === 'navigationStart')
            return Math.floor(timeOrigin);
        var navigation = navigationOf(timeline);
        var relative = navigation ? +navigation[name === 'domLoading' ? 'responseEnd' : name] || 0 : 0;
        if (relative > 0 || fetchMoments.indexOf(name) >= 0)
            return Math.floor(timeOrigin + relative);
        return 0;
    }

    var legacySlots = new WeakMap();

    function legacyTimeline(object) {
        var timeline = legacySlots.get(object);
        if (!timeline)
            throw illegalInvocation();
        return timeline;
    }

    var PerformanceTiming = defineInterface('PerformanceTiming', illegalConstructor('PerformanceTiming'));
    timingNames.forEach(function (name) {
        defineGetter(PerformanceTiming.prototype, name, function () { return epochTime(legacyTimeline(this), name); });
    });
    defineOperations(PerformanceTiming.prototype, {
        toJSON() {
            var timeline = legacyTimeline(this), json = {};
            timingNames.forEach(function (name) { json[name] = epochTime(timeline, name); });
            return json;
        }
    });

    var navigationTypes = { navigate: 0, reload: 1, back_forward: 2 };
    var PerformanceNavigation = defineInterface('PerformanceNavigation', illegalConstructor('PerformanceNavigation'));
    [['TYPE_NAVIGATE', 0], ['TYPE_RELOAD', 1], ['TYPE_BACK_FORWARD', 2], ['TYPE_RESERVED', 255]].forEach(function (constant) {
        defineProperty(PerformanceNavigation, constant[0], { value: constant[1], enumerable: true });
        defineProperty(PerformanceNavigation.prototype, constant[0], { value: constant[1], enumerable: true });
    });

    function navigationType(timeline) {
        var navigation = navigationOf(timeline);
        var type = navigation ? navigationTypes[navigation.type] : 0;
        return type === undefined ? 255 : type;
    }

    function redirectCount(timeline) {
        var navigation = navigationOf(timeline);
        return navigation ? +navigation.redirectCount || 0 : 0;
    }

    defineGetter(PerformanceNavigation.prototype, 'type', function () { return navigationType(legacyTimeline(this)); });
    defineGetter(PerformanceNavigation.prototype, 'redirectCount', function () { return redirectCount(legacyTimeline(this)); });
    defineOperations(PerformanceNavigation.prototype, {
        toJSON() {
            var timeline = legacyTimeline(this);
            return { type: navigationType(timeline), redirectCount: redirectCount(timeline) };
        }
    });

    // One of each per document, the same object at every read.
    function legacyObject(timeline, key, prototype) {
        if (!timeline[key]) {
            timeline[key] = Object.create(prototype);
            legacySlots.set(timeline[key], timeline);
        }
        return timeline[key];
    }

    defineGetter(performance, 'timing', function () { return legacyObject(currentTimeline(), 'timing', PerformanceTiming.prototype); });
    defineGetter(performance, 'navigation', function () { return legacyObject(currentTimeline(), 'navigationObject', PerformanceNavigation.prototype); });
    defineOperations(performance, {
        toJSON() {
            return { timeOrigin: timeOrigin, timing: performance.timing.toJSON(), navigation: performance.navigation.toJSON() };
        }
    });

    // ---------------------------------------------------------------- the page's load

    // The page's navigation entry reaches the observers waiting for one once its load event has ended,
    // with its whole duration; a buffered observer has had it all along. The host calls this with no
    // frame's script running, so the timeline it finds is the page's.
    host.loadEventEnded = function () {
        var page = currentTimeline();
        var navigation = navigationOf(page);
        if (navigation)
            queueEntry(page, navigation, false);
    };
})();
