// AbortController / AbortSignal (DOM §3.2).
//
// The signal used to be an object literal built inside the controller, with no AbortSignal
// constructor anywhere. Everything that touches a signal *through the controller* worked, so this
// looked complete — but the name itself did not exist, and a script that so much as mentions
// `AbortSignal` gets a ReferenceError, which aborts the whole script rather than the one line.
// That is what google.com's main bundle does, and it is where the bundle stopped once it began
// parsing at all: nothing it defines after that point came into existence.
//
// So AbortSignal is a real constructor with a real prototype. `aborted`, `reason` and `onabort`
// stay *own* properties of each signal, because the host reads them directly off the object; only
// the methods moved to the prototype, which is what makes `instanceof` and the static factories
// work.
(function () {
    function AbortSignal() {
        // The interface has no constructor of its own: a signal only ever comes from an
        // AbortController or from one of the statics below.
        throw new TypeError("Failed to construct 'AbortSignal': Illegal constructor.");
    }

    // A signal is an EventTarget, so inherit when there is one to inherit from — that is what
    // makes `signal instanceof EventTarget` true. Guarded rather than assumed: if EventTarget is
    // absent the plain prototype below still carries everything a signal is actually used for.
    if (typeof EventTarget === 'function' && EventTarget.prototype) {
        AbortSignal.prototype = Object.create(EventTarget.prototype);
        AbortSignal.prototype.constructor = AbortSignal;
    }

    function createSignal() {
        var signal = Object.create(AbortSignal.prototype);
        signal.aborted = false;
        signal.reason = undefined;
        signal.onabort = null;
        signal._listeners = [];
        return signal;
    }

    // The abort steps, shared by the controller and by AbortSignal.abort/timeout/any. Aborting an
    // already-aborted signal is a no-op, so a listener fires at most once however it was reached.
    function abortSignal(signal, reason) {
        if (signal.aborted) return;
        signal.aborted = true;
        signal.reason = reason !== undefined ? reason : new DOMException('The operation was aborted.', 'AbortError');
        var event = { type: 'abort', target: signal, currentTarget: signal };
        if (typeof signal.onabort === 'function') {
            try { signal.onabort(event); } catch (e) {}
        }
        var listeners = signal._listeners.slice();
        for (var i = 0; i < listeners.length; i++) {
            try { listeners[i].call(signal, event); } catch (e) {}
        }
    }

    AbortSignal.prototype.addEventListener = function (type, listener) {
        if (type !== 'abort' || typeof listener !== 'function') return;
        if (this._listeners.indexOf(listener) === -1) this._listeners.push(listener);
    };

    AbortSignal.prototype.removeEventListener = function (type, listener) {
        if (type !== 'abort') return;
        var index = this._listeners.indexOf(listener);
        if (index !== -1) this._listeners.splice(index, 1);
    };

    AbortSignal.prototype.throwIfAborted = function () {
        if (this.aborted) throw (this.reason !== undefined ? this.reason : new DOMException('The operation was aborted.', 'AbortError'));
    };

    // AbortSignal.abort(reason) — a signal that is already aborted.
    AbortSignal.abort = function (reason) {
        var signal = createSignal();
        abortSignal(signal, reason);
        return signal;
    };

    // AbortSignal.timeout(ms) — aborts with a TimeoutError, which is deliberately not an
    // AbortError: code that distinguishes "the user cancelled" from "it took too long" reads
    // reason.name to tell them apart.
    AbortSignal.timeout = function (milliseconds) {
        var signal = createSignal();
        setTimeout(function () {
            abortSignal(signal, new DOMException('The operation was aborted due to timeout.', 'TimeoutError'));
        }, milliseconds);
        return signal;
    };

    // AbortSignal.any(signals) — follows whichever aborts first, and is already aborted if any of
    // them is, so a caller cannot miss an abort that happened before it composed them.
    AbortSignal.any = function (signals) {
        var composite = createSignal();
        var sources = signals ? Array.prototype.slice.call(signals) : [];
        for (var i = 0; i < sources.length; i++) {
            if (sources[i] && sources[i].aborted) {
                abortSignal(composite, sources[i].reason);
                return composite;
            }
        }
        for (var j = 0; j < sources.length; j++) {
            (function (source) {
                if (!source || typeof source.addEventListener !== 'function') return;
                source.addEventListener('abort', function () { abortSignal(composite, source.reason); });
            })(sources[j]);
        }
        return composite;
    };

    function AbortController() {
        this.signal = createSignal();
    }

    AbortController.prototype.abort = function (reason) {
        abortSignal(this.signal, reason);
    };

    globalThis.AbortSignal = AbortSignal;
    globalThis.AbortController = AbortController;
})();

// CSS Font Loading (css-font-loading-3) — FontFace and the document.fonts FontFaceSet.
//
// The API did not exist, so `document.fonts` was undefined and `document.fonts.load(…)` a
// TypeError. That is not confined to the font code that asks for it: on google.com the very first
// inline script is a font preloader whose whole body is one `document.fonts.load` loop, so the
// script dies on its first statement and everything it would have gone on to do never happens.
//
// Both halves ship together deliberately. A FontFaceSet with no FontFace constructor is the shape
// that hid the AbortSignal gap for so long — `document.fonts.add(new FontFace(…))` is the ordinary
// way to use this API, and it needs both names to exist.
//
// What it models: Broiler resolves fonts synchronously against what it already has when it lays
// text out, so from a page's point of view there is never a load in flight. `status` is therefore
// "loaded", `ready` is already resolved, and `check()` is true. `load()` resolves rather than
// rejecting — a page calling it is asking Broiler to *start* a load it has no pending work for,
// and the failure mode that matters is a promise that never settles, which would strand a page
// waiting behind `document.fonts.ready` before it renders anything.
(function () {
    function FontFace(family, source, descriptors) {
        this.family = family !== undefined ? String(family) : '';
        this.style = 'normal';
        this.weight = 'normal';
        this.stretch = 'normal';
        this.unicodeRange = 'U+0-10FFFF';
        this.variant = 'normal';
        this.featureSettings = 'normal';
        this.variationSettings = 'normal';
        this.display = 'auto';
        this.ascentOverride = 'normal';
        this.descentOverride = 'normal';
        this.lineGapOverride = 'normal';

        if (descriptors) {
            for (var key in descriptors) {
                if (Object.prototype.hasOwnProperty.call(descriptors, key)) this[key] = descriptors[key];
            }
        }

        // A FontFace built from a source is "unloaded" until load() is called; one whose source is
        // already binary data (an ArrayBuffer rather than a url()) is loaded on construction, per
        // the spec's split between the two constructor forms.
        var isBinarySource = source !== undefined && typeof source !== 'string';
        this.status = isBinarySource ? 'loaded' : 'unloaded';
        this._source = source;

        var self = this;
        this.loaded = isBinarySource
            ? Promise.resolve(self)
            : new Promise(function (resolve) { self._resolveLoaded = resolve; });
        if (isBinarySource) this._resolveLoaded = null;
    }

    FontFace.prototype.load = function () {
        if (this.status === 'unloaded' || this.status === 'loading') {
            this.status = 'loaded';
            if (this._resolveLoaded) { this._resolveLoaded(this); this._resolveLoaded = null; }
        }
        return this.loaded;
    };

    function FontFaceSet() {
        this._faces = [];
        this.onloading = null;
        this.onloadingdone = null;
        this.onloadingerror = null;
        this._listeners = {};
    }

    // Set-like, which is what iteration and `size` on document.fonts rely on.
    Object.defineProperty(FontFaceSet.prototype, 'size', {
        get: function () { return this._faces.length; },
        configurable: true
    });

    // Nothing is ever in flight, so the set is loaded and ready from the start. `ready` is cached
    // rather than rebuilt per access: a page may await it more than once, and each access handing
    // back a different promise is a subtle way to strand one of them.
    Object.defineProperty(FontFaceSet.prototype, 'status', {
        get: function () { return 'loaded'; },
        configurable: true
    });

    Object.defineProperty(FontFaceSet.prototype, 'ready', {
        get: function () {
            if (!this._ready) this._ready = Promise.resolve(this);
            return this._ready;
        },
        configurable: true
    });

    FontFaceSet.prototype.add = function (face) {
        if (face && this._faces.indexOf(face) === -1) this._faces.push(face);
        return this;
    };

    FontFaceSet.prototype.delete = function (face) {
        var index = this._faces.indexOf(face);
        if (index === -1) return false;
        this._faces.splice(index, 1);
        return true;
    };

    FontFaceSet.prototype.clear = function () { this._faces.length = 0; };
    FontFaceSet.prototype.has = function (face) { return this._faces.indexOf(face) !== -1; };

    FontFaceSet.prototype.forEach = function (callback, thisArg) {
        for (var i = 0; i < this._faces.length; i++) {
            callback.call(thisArg, this._faces[i], this._faces[i], this);
        }
    };

    FontFaceSet.prototype.values = function () { return this._faces.slice()[Symbol.iterator](); };
    FontFaceSet.prototype.keys = function () { return this.values(); };
    FontFaceSet.prototype.entries = function () {
        var pairs = [];
        for (var i = 0; i < this._faces.length; i++) pairs.push([this._faces[i], this._faces[i]]);
        return pairs[Symbol.iterator]();
    };
    if (typeof Symbol !== 'undefined' && Symbol.iterator) {
        FontFaceSet.prototype[Symbol.iterator] = function () { return this.values(); };
    }

    // load()/check() take a CSS `font` shorthand, and css-font-loading-3 says an unparsable one is a
    // SyntaxError. Only an absent or empty string used to throw: anything non-empty was waved
    // through, so `document.fonts.check('not-a-font')` answered `true` where every browser throws.
    // The reason given for that was risk — rejecting a shorthand Broiler merely failed to parse
    // would break pages over a diagnostic it could not produce — and the answer to it is to parse
    // the shorthand properly rather than to accept everything, which is what this does. The grammar
    // is small and closed (CSS Fonts 4 §"font"), so accepting exactly it is not an approximation:
    //
    //     font = <system-family-name>
    //          | [ <font-style> || <font-variant-css2> || <font-weight> || <font-stretch-css3> ]?
    //            <font-size> [ / <line-height> ]? <font-family>
    //
    // What has NOT changed is the modelling: a shorthand that parses still answers `true` from
    // check() and resolves from load(), because Broiler resolves fonts synchronously and there is
    // never a load in flight. This only stops it claiming to understand strings that are not fonts.
    var SYSTEM_FONTS = ['caption', 'icon', 'menu', 'message-box', 'small-caption', 'status-bar'];
    var FONT_STYLES = ['normal', 'italic', 'oblique'];
    var FONT_VARIANTS = ['normal', 'small-caps'];
    var FONT_WEIGHTS = ['normal', 'bold', 'bolder', 'lighter'];
    var FONT_STRETCHES = ['normal', 'ultra-condensed', 'extra-condensed', 'condensed', 'semi-condensed',
                          'semi-expanded', 'expanded', 'extra-expanded', 'ultra-expanded'];
    var ABSOLUTE_SIZES = ['xx-small', 'x-small', 'small', 'medium', 'large', 'x-large', 'xx-large',
                          'xxx-large', 'larger', 'smaller'];

    // Whitespace-separated, but quotes and parentheses hold together: a family name may be quoted
    // and a size may be `calc(1em + 2px)`, and neither survives a naive split.
    function fontTokens(text) {
        var tokens = [], current = '', quote = null, depth = 0;
        for (var i = 0; i < text.length; i++) {
            var ch = text[i];
            if (quote) {
                current += ch;
                if (ch === quote) quote = null;
                continue;
            }
            if (ch === '"' || ch === "'") { quote = ch; current += ch; continue; }
            if (ch === '(') depth++;
            if (ch === ')') depth--;
            if (depth === 0 && /\s/.test(ch)) {
                if (current !== '') { tokens.push(current); current = ''; }
                continue;
            }
            current += ch;
        }
        if (current !== '') tokens.push(current);
        return quote || depth !== 0 ? null : tokens;
    }

    function isFontSize(token) {
        var lower = token.toLowerCase();
        if (ABSOLUTE_SIZES.indexOf(lower) !== -1) return true;
        if (/^calc\(.+\)$/i.test(token)) return true;
        // A length or percentage: a number with a unit. A bare number is not a font-size.
        return /^[+-]?(\d+\.?\d*|\.\d+)(%|[a-z]+)$/i.test(token);
    }

    function isFontWeight(token) {
        if (FONT_WEIGHTS.indexOf(token.toLowerCase()) !== -1) return true;
        return /^\d+(\.\d+)?$/.test(token);
    }

    // Each comma-separated family is either a quoted string or a run of identifiers. An identifier
    // may not start with a digit, which is what makes `12px 12px serif` a malformed family rather
    // than a family called "12px serif".
    function isFontFamilyList(text) {
        if (text.trim() === '') return false;
        var families = [];
        var current = '', quote = null;
        for (var i = 0; i < text.length; i++) {
            var ch = text[i];
            if (quote) { current += ch; if (ch === quote) quote = null; continue; }
            if (ch === '"' || ch === "'") { quote = ch; current += ch; continue; }
            if (ch === ',') { families.push(current); current = ''; continue; }
            current += ch;
        }
        if (quote) return false;
        families.push(current);

        for (var f = 0; f < families.length; f++) {
            var name = families[f].trim();
            if (name === '') return false;
            if (/^"[^"]*"$/.test(name) || /^'[^']*'$/.test(name)) continue;
            var words = name.split(/\s+/);
            for (var w = 0; w < words.length; w++) {
                if (!/^-?[A-Za-z_\u00a0-\uffff][A-Za-z0-9_\u00a0-\uffff-]*$/.test(words[w])) return false;
            }
        }
        return true;
    }

    function isFontShorthand(font) {
        if (font === undefined || font === null) return false;
        var text = String(font).trim();
        if (text === '') return false;
        if (SYSTEM_FONTS.indexOf(text.toLowerCase()) !== -1) return true;

        var tokens = fontTokens(text);
        if (!tokens || tokens.length === 0) return false;

        // The optional leading run, in any order and each at most once. `oblique` may carry an angle.
        var seen = {}, index = 0;
        for (; index < tokens.length; index++) {
            var token = tokens[index], lower = token.toLowerCase();
            var slot = FONT_STYLES.indexOf(lower) !== -1 ? 'style'
                : FONT_VARIANTS.indexOf(lower) !== -1 ? 'variant'
                : FONT_STRETCHES.indexOf(lower) !== -1 ? 'stretch'
                : isFontWeight(token) ? 'weight'
                : null;
            // `normal` is legal in several slots and says nothing; it never blocks a later one.
            if (slot === null) break;
            if (lower !== 'normal') {
                if (seen[slot]) return false;
                seen[slot] = true;
            }
            if (lower === 'oblique' && index + 1 < tokens.length && /^[+-]?[\d.]+deg$/i.test(tokens[index + 1])) index++;
        }

        if (index >= tokens.length) return false;

        // <font-size> [ / <line-height> ]?, which may be written glued together or spaced apart.
        var size = tokens[index++];
        var slash = size.indexOf('/');
        if (slash !== -1) {
            if (slash === 0 || slash === size.length - 1) return false;
            if (!isFontSize(size.slice(0, slash))) return false;
        } else if (!isFontSize(size)) {
            return false;
        }

        return index < tokens.length && isFontFamilyList(tokens.slice(index).join(' '));
    }

    function requireFontShorthand(font) {
        if (!isFontShorthand(font)) {
            throw new DOMException("Failed to parse the 'font' property.", 'SyntaxError');
        }
    }

    FontFaceSet.prototype.load = function (font, text) {
        try {
            requireFontShorthand(font);
        } catch (e) {
            return Promise.reject(e);
        }

        // Resolves with the faces this set holds for the family, which is the empty list unless the
        // page added FontFace objects itself — Broiler's own fonts are not modelled as FontFace
        // instances, so claiming them here would hand back objects that describe nothing.
        var matching = [];
        for (var i = 0; i < this._faces.length; i++) {
            var face = this._faces[i];
            if (face.status !== 'loaded') face.load();
            if (String(font).indexOf(face.family) !== -1) matching.push(face);
        }
        return Promise.resolve(matching);
    };

    FontFaceSet.prototype.check = function (font, text) {
        requireFontShorthand(font);
        // Text is laid out with whatever Broiler resolves the family to, so from the page's side
        // the font it asked about is always available to draw with.
        return true;
    };

    FontFaceSet.prototype.addEventListener = function (type, listener) {
        if (typeof listener !== 'function') return;
        if (!this._listeners[type]) this._listeners[type] = [];
        if (this._listeners[type].indexOf(listener) === -1) this._listeners[type].push(listener);
    };

    FontFaceSet.prototype.removeEventListener = function (type, listener) {
        var listeners = this._listeners[type];
        if (!listeners) return;
        var index = listeners.indexOf(listener);
        if (index !== -1) listeners.splice(index, 1);
    };

    globalThis.FontFace = FontFace;
    globalThis.FontFaceSet = FontFaceSet;

    if (typeof document !== 'undefined' && document && !document.fonts) {
        document.fonts = new FontFaceSet();
    }
})();
