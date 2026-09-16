// Broiler HtmlBridge — content-rendering polyfills
// version: 1  (Google Search Compliance content-rendering / fidelity stubs)
//
// Pure-JavaScript polyfills installed on a fresh browsing-context global. This asset is embedded in
// Broiler.HtmlBridge.Dom and evaluated once per document by DomBridge.RegisterContentRenderingPolyfills
// (Phase 3 work item 6 — externalized from inline C# string literals, byte-for-byte at the time). Each
// stub is fixed forward here as a real page finds its edges, so this is the definition, not a copy of
// one; the C#-interop polyfills (document.cookie, crypto, DOMException, Node, SVGLength constructors)
// remain in DomBridge/Registration/Registration.cs because they are host-driven, not pure JS.
//
// The bundle continues in content-rendering-polyfills.url.js and
// content-rendering-polyfills.abort-and-fonts.js. PolyfillAssets joins the three, in that order, into
// the one script that is evaluated, so a declaration here is still visible to the files after it.

// Image() constructor — returns stub object with src property
function Image(width, height) {
    this.src = '';
    this.width = width || 0;
    this.height = height || 0;
    this.alt = '';
    this.complete = false;
    this.naturalWidth = 0;
    this.naturalHeight = 0;
    this.onload = null;
    this.onerror = null;
    this.addEventListener = function() {};
    this.removeEventListener = function() {};
}

// IntersectionObserver — stub that immediately invokes callback
function IntersectionObserver(callback, options) {
    this._callback = callback;
    this._targets = [];
}
IntersectionObserver.prototype.observe = function(target) {
    this._targets.push(target);
    // Immediately report as intersecting
    var entry = {
        target: target,
        isIntersecting: true,
        intersectionRatio: 1.0,
        boundingClientRect: { top: 0, left: 0, bottom: 0, right: 0, width: 0, height: 0 },
        intersectionRect: { top: 0, left: 0, bottom: 0, right: 0, width: 0, height: 0 },
        rootBounds: null,
        time: 0
    };
    try { this._callback([entry], this); } catch(e) {}
};
IntersectionObserver.prototype.unobserve = function(target) {
    this._targets = this._targets.filter(function(t) { return t !== target; });
};
IntersectionObserver.prototype.disconnect = function() {
    this._targets = [];
};
IntersectionObserver.prototype.takeRecords = function() {
    return [];
};

// ResizeObserver — no-op stub
function ResizeObserver(callback) {
    this._callback = callback;
}
ResizeObserver.prototype.observe = function() {};
ResizeObserver.prototype.unobserve = function() {};
ResizeObserver.prototype.disconnect = function() {};

// TextEncoder / TextDecoder — basic UTF-8 stubs
function TextEncoder() {
    this.encoding = 'utf-8';
}
TextEncoder.prototype.encode = function(str) {
    str = str || '';
    var arr = [];
    for (var i = 0; i < str.length; i++) {
        var c = str.charCodeAt(i);
        if (c < 0x80) {
            arr.push(c);
        } else if (c < 0x800) {
            arr.push(0xC0 | (c >> 6));
            arr.push(0x80 | (c & 0x3F));
        } else if (c >= 0xD800 && c <= 0xDBFF && i + 1 < str.length) {
            var next = str.charCodeAt(i + 1);
            if (next >= 0xDC00 && next <= 0xDFFF) {
                var cp = ((c - 0xD800) << 10) + (next - 0xDC00) + 0x10000;
                arr.push(0xF0 | (cp >> 18));
                arr.push(0x80 | ((cp >> 12) & 0x3F));
                arr.push(0x80 | ((cp >> 6) & 0x3F));
                arr.push(0x80 | (cp & 0x3F));
                i++;
            } else {
                arr.push(0xEF); arr.push(0xBF); arr.push(0xBD);
            }
        } else {
            arr.push(0xE0 | (c >> 12));
            arr.push(0x80 | ((c >> 6) & 0x3F));
            arr.push(0x80 | (c & 0x3F));
        }
    }
    return new Uint8Array(arr);
};
TextEncoder.prototype.encodeInto = function(str, dest) {
    var encoded = this.encode(str);
    var len = Math.min(encoded.length, dest.length);
    for (var i = 0; i < len; i++) dest[i] = encoded[i];
    return { read: str.length, written: len };
};

function TextDecoder(encoding) {
    this.encoding = (encoding || 'utf-8').toLowerCase();
    this.fatal = false;
    this.ignoreBOM = false;
}
TextDecoder.prototype.decode = function(input) {
    if (!input || input.length === 0) return '';
    var bytes = input instanceof Uint8Array ? input : new Uint8Array(input);
    var result = '';
    var len = bytes.length;
    for (var i = 0; i < len; ) {
        var b = bytes[i];
        if (b < 0x80) {
            result += String.fromCharCode(b);
            i++;
        } else if ((b & 0xE0) === 0xC0 && i + 1 < len) {
            result += String.fromCharCode(((b & 0x1F) << 6) | (bytes[i+1] & 0x3F));
            i += 2;
        } else if ((b & 0xF0) === 0xE0 && i + 2 < len) {
            result += String.fromCharCode(((b & 0x0F) << 12) | ((bytes[i+1] & 0x3F) << 6) | (bytes[i+2] & 0x3F));
            i += 3;
        } else if ((b & 0xF8) === 0xF0 && i + 3 < len) {
            var cp = ((b & 0x07) << 18) | ((bytes[i+1] & 0x3F) << 12) | ((bytes[i+2] & 0x3F) << 6) | (bytes[i+3] & 0x3F);
            cp -= 0x10000;
            result += String.fromCharCode(0xD800 + (cp >> 10), 0xDC00 + (cp & 0x3FF));
            i += 4;
        } else {
            result += '\uFFFD';
            i++;
        }
    }
    return result;
};
