// TextEncoder / TextDecoder — UTF-8 only.
//
// Pure JavaScript, and needing nothing of a document, so it is one asset of its own: the page's
// content-rendering bundle includes it (PolyfillAssets.ContentRenderingParts), and so does a
// worker's global (PolyfillAssets.Worker), which has no document at all.

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
