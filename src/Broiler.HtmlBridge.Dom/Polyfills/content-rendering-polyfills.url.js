// URL / URLSearchParams — WHATWG URL Standard (https://url.spec.whatwg.org/)
//
// Both constructor arguments are USVStrings, so a browser stringifies whatever it is handed:
// `new URL( location )` and `new URL( path, location )` are ordinary calls, and so is
// `new URL( anchor.href, someOtherURL )`. This polyfill used to call `url.match(...)` on the
// argument exactly as given, which made every one of those throw "undefined is not a function"
// from inside the constructor — a Location has no `match`. That is where mediawiki.org's start
// page stopped: CentralNotice's setInitialData opens with
// `( new URL( location ) ).searchParams.forEach( … )`, the throw escaped through
// reallyChooseAndMaybeDisplay, and the module chain that was still loading behind it died with
// it. So the first thing the parser does now is coerce.
//
// Parsing follows the standard's basic URL parser closely enough for content scripts: special
// schemes and their default ports, opaque ("cannot-be-a-base") paths such as `data:`, `mailto:`
// and `blob:`, authorities carrying credentials and IPv6 literals, dot-segment removal, and
// relative references of every shape a page produces — scheme-relative (`//meta.example/w/x`,
// which the old code resolved against a base to `https://base-host//meta.example/w/x`),
// absolute-path, query-only, fragment-only and dotted relative paths. A reference that cannot be
// resolved throws a TypeError the way the constructor does in a browser, instead of yielding an
// object whose components are quietly wrong.
//
// The components are accessors over one parsed record rather than a snapshot of data properties,
// because pages mutate a URL and read it back: `u.searchParams.set('x', 1); u.toString()` and
// `u.hash = '#top'; u.href` both have to see the edit.

var URL_SPECIAL_PORT = { 'ftp:': '21', 'file:': '', 'http:': '80', 'https:': '443', 'ws:': '80', 'wss:': '443' };
var URL_PATH_UNSAFE = '"<>`{}\\';
var URL_QUERY_UNSAFE = '"<>#';
var URL_FRAGMENT_UNSAFE = '"<>`';

function urlIsSpecial(scheme) {
    return Object.prototype.hasOwnProperty.call(URL_SPECIAL_PORT, scheme);
}

// Drop the leading/trailing C0-control-or-space the parser is required to ignore, and remove
// every tab and newline from the middle of the input.
function urlStripInput(input) {
    input = input.replace(/[\t\n\r]/g, '');
    var start = 0;
    var end = input.length;
    while (start < end && input.charCodeAt(start) <= 0x20) start++;
    while (end > start && input.charCodeAt(end - 1) <= 0x20) end--;
    return input.substring(start, end);
}

// Percent-encode the code points not allowed to appear literally in this part of a URL. `unsafe`
// is the part-specific set; C0 controls, space, DEL and everything above ASCII are always encoded
// (as UTF-8, which is what encodeURIComponent produces).
function urlPercentEncode(s, unsafe) {
    var out = '';
    for (var i = 0; i < s.length; i++) {
        var c = s.charAt(i);
        var code = s.charCodeAt(i);
        if (code >= 0xD800 && code <= 0xDBFF && i + 1 < s.length) {
            var low = s.charCodeAt(i + 1);
            if (low >= 0xDC00 && low <= 0xDFFF) {
                out += encodeURIComponent(s.substr(i, 2));
                i++;
                continue;
            }
        }
        if (code >= 0xD800 && code <= 0xDFFF) {
            out += '%EF%BF%BD'; // A lone surrogate is not a scalar value; U+FFFD stands in for it.
        } else if (code <= 0x20 || code >= 0x7F || unsafe.indexOf(c) !== -1) {
            out += encodeURIComponent(c);
        } else {
            out += c;
        }
    }
    return out;
}

function urlNewRecord() {
    return {
        scheme: '', username: '', password: '', host: null,
        port: '', path: '', query: null, fragment: null, opaque: false
    };
}

// A file: URL has no port at all; every other port is a 16-bit number.
function urlIsValidPort(port, scheme) {
    if (port === '') return true;
    if (scheme === 'file:') return false;
    return /^\d+$/.test(port) && parseInt(port, 10) <= 65535;
}

// A port equal to its scheme's default is not serialized, and a port is stored in its shortest
// form ("0080" and "80" are the same port).
function urlNormalizePort(port, scheme) {
    if (port === '') return '';
    var normalized = String(parseInt(port, 10));
    return normalized === URL_SPECIAL_PORT[scheme] ? '' : normalized;
}

// Dot-segment removal: "a/b/../c" -> "a/c", with a trailing "." or ".." leaving the trailing
// slash behind. An absolute path never pops past its leading empty segment.
function urlRemoveDotSegments(path) {
    if (path.indexOf('.') === -1 && path.toLowerCase().indexOf('%2e') === -1) return path;
    var segments = path.split('/');
    var floor = path.charAt(0) === '/' ? 1 : 0;
    var out = [];
    for (var i = 0; i < segments.length; i++) {
        var seg = segments[i];
        var lower = seg.toLowerCase();
        if (lower === '.' || lower === '%2e') {
            if (i === segments.length - 1) out.push('');
            continue;
        }
        if (lower === '..' || lower === '.%2e' || lower === '%2e.' || lower === '%2e%2e') {
            if (out.length > floor) out.pop();
            if (i === segments.length - 1) out.push('');
            continue;
        }
        out.push(seg);
    }
    return out.join('/');
}

// Split "path[?query][#fragment]" off the tail of an input and store it on the record. The host
// must already be set: a URL that has one always has an absolute path.
function urlSetPathQueryFragment(record, s, special) {
    var hash = s.indexOf('#');
    if (hash !== -1) {
        record.fragment = urlPercentEncode(s.substring(hash + 1), URL_FRAGMENT_UNSAFE);
        s = s.substring(0, hash);
    }
    var question = s.indexOf('?');
    if (question !== -1) {
        record.query = urlPercentEncode(s.substring(question + 1), URL_QUERY_UNSAFE);
        s = s.substring(0, question);
    }
    if (special) s = s.replace(/\\/g, '/');
    if (record.host !== null && s.charAt(0) !== '/') s = '/' + s;
    record.path = urlPercentEncode(urlRemoveDotSegments(s), URL_PATH_UNSAFE);
}

// "host:port/rest" — the authority with its leading slashes already removed. Returns false when
// the authority is not a valid one for this scheme (an empty host on a special scheme other than
// file:, a non-numeric port, an unterminated IPv6 literal).
function urlParseAuthority(record, rest) {
    var special = urlIsSpecial(record.scheme);
    var end = rest.length;
    for (var i = 0; i < rest.length; i++) {
        var c = rest.charAt(i);
        if (c === '/' || c === '?' || c === '#' || (special && c === '\\')) { end = i; break; }
    }
    var authority = rest.substring(0, end);
    var remainder = rest.substring(end);

    var at = authority.lastIndexOf('@');
    if (at !== -1) {
        var credentials = authority.substring(0, at);
        authority = authority.substring(at + 1);
        var colon = credentials.indexOf(':');
        record.username = colon === -1 ? credentials : credentials.substring(0, colon);
        record.password = colon === -1 ? '' : credentials.substring(colon + 1);
    }

    // An IPv6 literal keeps its brackets and is full of colons, so the port separator is only
    // looked for past the closing bracket.
    var hostEnd = 0;
    if (authority.charAt(0) === '[') {
        hostEnd = authority.indexOf(']') + 1;
        if (hostEnd === 0) return false;
    }
    var portColon = authority.indexOf(':', hostEnd);
    var host = portColon === -1 ? authority : authority.substring(0, portColon);
    var port = portColon === -1 ? '' : authority.substring(portColon + 1);

    if (!urlIsValidPort(port, record.scheme)) return false;
    if (host === '' && special && record.scheme !== 'file:') return false;

    record.host = host.toLowerCase();
    record.port = urlNormalizePort(port, record.scheme);
    urlSetPathQueryFragment(record, remainder, special);
    return true;
}

// file: has no authority unless exactly two slashes introduce one, and a Windows drive letter is
// never a host: "file:/C:/x", "file:///C:/x" and "file:C:/x" all mean the same local path.
function urlParseFile(record, rest) {
    var body = rest.replace(/^[\\/]*/, '');
    var namesHost = /^[\\/]{2}/.test(rest) && !/^[\\/]{3}/.test(rest) &&
        !/^[A-Za-z][:|](?:[\\/?#]|$)/.test(body);
    if (namesHost) return urlParseAuthority(record, rest.substring(2));
    record.host = '';
    urlSetPathQueryFragment(record, '/' + body, true);
    return true;
}

// The basic URL parser. Returns a record, or null for the failure the constructor reports as a
// TypeError.
function urlParse(input, base) {
    input = urlStripInput(String(input));
    var record = urlNewRecord();

    var schemeMatch = /^([A-Za-z][A-Za-z0-9+\-.]*):/.exec(input);
    if (schemeMatch) {
        var scheme = schemeMatch[1].toLowerCase() + ':';
        var rest = input.substring(schemeMatch[0].length);
        var special = urlIsSpecial(scheme);

        // "special relative or authority state": `https:foo` against an https: base is a relative
        // reference, not a fresh authority — only `https://foo` and `https:/foo` name a host.
        if (special && base && base.scheme === scheme && !/^[\\/]{2}/.test(rest)) {
            input = rest;
        } else {
            record.scheme = scheme;
            if (scheme === 'file:') return urlParseFile(record, rest) ? record : null;
            if (special) return urlParseAuthority(record, rest.replace(/^[\\/]*/, '')) ? record : null;
            if (rest.substring(0, 2) === '//') return urlParseAuthority(record, rest.substring(2)) ? record : null;

            // Opaque path — data:, mailto:, blob:, javascript:, tel: … There is no host and no
            // dot-segment removal; the path is what was written.
            record.opaque = true;
            var oHash = rest.indexOf('#');
            if (oHash !== -1) {
                record.fragment = urlPercentEncode(rest.substring(oHash + 1), URL_FRAGMENT_UNSAFE);
                rest = rest.substring(0, oHash);
            }
            var oQuestion = rest.indexOf('?');
            if (oQuestion !== -1) {
                record.query = urlPercentEncode(rest.substring(oQuestion + 1), URL_QUERY_UNSAFE);
                rest = rest.substring(0, oQuestion);
            }
            record.path = rest;
            return record;
        }
    }

    // A relative reference only means something against a base.
    if (!base) return null;
    record.scheme = base.scheme;
    var baseSpecial = urlIsSpecial(base.scheme);
    if (baseSpecial) input = input.replace(/\\/g, '/');

    if (base.opaque) {
        // A cannot-be-a-base URL can only be given a new fragment.
        if (input.charAt(0) !== '#') return null;
        record.opaque = true;
        record.path = base.path;
        record.query = base.query;
        record.fragment = urlPercentEncode(input.substring(1), URL_FRAGMENT_UNSAFE);
        return record;
    }

    // "//host/path" keeps only the base's scheme. A special scheme skips however many slashes
    // were written; a non-special one takes exactly the two; file: counts them (see urlParseFile).
    if (input.substring(0, 2) === '//') {
        if (base.scheme === 'file:') return urlParseFile(record, input) ? record : null;
        var authority = baseSpecial ? input.replace(/^\/*/, '') : input.substring(2);
        return urlParseAuthority(record, authority) ? record : null;
    }

    record.username = base.username;
    record.password = base.password;
    record.host = base.host;
    record.port = base.port;

    if (input === '') {
        record.path = base.path;
        record.query = base.query;
        return record;
    }
    if (input.charAt(0) === '#') {
        record.path = base.path;
        record.query = base.query;
        record.fragment = urlPercentEncode(input.substring(1), URL_FRAGMENT_UNSAFE);
        return record;
    }
    if (input.charAt(0) === '?' || input.charAt(0) === '/') {
        urlSetPathQueryFragment(record, (input.charAt(0) === '?' ? base.path : '') + input, baseSpecial);
        return record;
    }

    // Merge: the base up to and including its last slash, then the reference.
    var lastSlash = base.path.lastIndexOf('/');
    var prefix = lastSlash === -1 ? (base.host !== null ? '/' : '') : base.path.substring(0, lastSlash + 1);
    urlSetPathQueryFragment(record, prefix + input, baseSpecial);
    return record;
}

function urlSerialize(record) {
    var authority = '';
    if (record.host !== null) {
        var credentials = '';
        if (record.username !== '' || record.password !== '') {
            credentials = record.username + (record.password !== '' ? ':' + record.password : '') + '@';
        }
        authority = '//' + credentials + record.host + (record.port !== '' ? ':' + record.port : '');
    }
    return record.scheme + authority + record.path +
        (record.query !== null ? '?' + record.query : '') +
        (record.fragment !== null ? '#' + record.fragment : '');
}

// application/x-www-form-urlencoded: '+' is a space, and a malformed escape is kept verbatim
// rather than thrown — decodeURIComponent rejects a stray '%', a page's query string does not.
function urlFormDecode(s) {
    s = s.replace(/\+/g, ' ');
    if (s.indexOf('%') === -1) return s;
    try {
        return decodeURIComponent(s);
    } catch (e) {
        return s;
    }
}

function urlFormEncode(s) {
    return encodeURIComponent(String(s)).replace(/%20/g, '+').replace(/[!'()~*]/g, function(c) {
        return '%' + c.charCodeAt(0).toString(16).toUpperCase();
    });
}

function urlMakeIterator(items) {
    var i = 0;
    var iterator = {
        next: function() {
            return i < items.length ? { value: items[i++], done: false } : { value: undefined, done: true };
        }
    };
    if (typeof Symbol !== 'undefined' && Symbol.iterator) {
        iterator[Symbol.iterator] = function() { return this; };
    }
    return iterator;
}

function URLSearchParams(init) {
    this._params = [];
    this._url = this._url || null;
    if (init === undefined || init === null) return;
    if (init instanceof URLSearchParams) {
        for (var k = 0; k < init._params.length; k++) {
            this._params.push([init._params[k][0], init._params[k][1]]);
        }
        return;
    }
    if (typeof init === 'object') {
        if (typeof init.length === 'number') {
            // A sequence of [name, value] pairs.
            for (var n = 0; n < init.length; n++) {
                var pair = init[n];
                if (!pair || pair.length !== 2) {
                    throw new TypeError('Failed to construct \'URLSearchParams\': Each entry must have two elements');
                }
                this._params.push([String(pair[0]), String(pair[1])]);
            }
            return;
        }
        var keys = Object.keys(init);
        for (var j = 0; j < keys.length; j++) {
            this._params.push([keys[j], String(init[keys[j]])]);
        }
        return;
    }
    var s = String(init);
    if (s.charAt(0) === '?') s = s.substring(1);
    if (s === '') return;
    var pairs = s.split('&');
    for (var i = 0; i < pairs.length; i++) {
        if (pairs[i] === '') continue;
        var eq = pairs[i].indexOf('=');
        var name = eq === -1 ? pairs[i] : pairs[i].substring(0, eq);
        var value = eq === -1 ? '' : pairs[i].substring(eq + 1);
        this._params.push([urlFormDecode(name), urlFormDecode(value)]);
    }
}

// A URL's searchParams is the same object across reads, and writing through it rewrites the
// URL's query — `u.searchParams.set('a', 1); String(u)` has to show the change.
URLSearchParams.prototype._notify = function() {
    if (!this._url) return;
    var serialized = this.toString();
    this._url._record.query = serialized === '' ? null : serialized;
};
URLSearchParams.prototype.get = function(name) {
    name = String(name);
    for (var i = 0; i < this._params.length; i++) {
        if (this._params[i][0] === name) return this._params[i][1];
    }
    return null;
};
URLSearchParams.prototype.getAll = function(name) {
    name = String(name);
    var r = [];
    for (var i = 0; i < this._params.length; i++) {
        if (this._params[i][0] === name) r.push(this._params[i][1]);
    }
    return r;
};
URLSearchParams.prototype.has = function(name) { return this.get(name) !== null; };
URLSearchParams.prototype.set = function(name, value) {
    name = String(name);
    var found = false;
    for (var i = 0; i < this._params.length; i++) {
        if (this._params[i][0] === name) {
            if (!found) { this._params[i][1] = String(value); found = true; }
            else { this._params.splice(i, 1); i--; }
        }
    }
    if (!found) this._params.push([name, String(value)]);
    this._notify();
};
URLSearchParams.prototype.append = function(name, value) {
    this._params.push([String(name), String(value)]);
    this._notify();
};
URLSearchParams.prototype['delete'] = function(name) {
    name = String(name);
    this._params = this._params.filter(function(p) { return p[0] !== name; });
    this._notify();
};
URLSearchParams.prototype.sort = function() {
    // Stable, by name, comparing code units.
    var indexed = this._params.map(function(p, i) { return [p, i]; });
    indexed.sort(function(a, b) {
        if (a[0][0] < b[0][0]) return -1;
        if (a[0][0] > b[0][0]) return 1;
        return a[1] - b[1];
    });
    this._params = indexed.map(function(e) { return e[0]; });
    this._notify();
};
URLSearchParams.prototype.toString = function() {
    return this._params.map(function(p) {
        return urlFormEncode(p[0]) + '=' + urlFormEncode(p[1]);
    }).join('&');
};
URLSearchParams.prototype.forEach = function(cb, thisArg) {
    for (var i = 0; i < this._params.length; i++) {
        cb.call(thisArg, this._params[i][1], this._params[i][0], this);
    }
};
URLSearchParams.prototype.entries = function() {
    return urlMakeIterator(this._params.map(function(p) { return [p[0], p[1]]; }));
};
URLSearchParams.prototype.keys = function() {
    return urlMakeIterator(this._params.map(function(p) { return p[0]; }));
};
URLSearchParams.prototype.values = function() {
    return urlMakeIterator(this._params.map(function(p) { return p[1]; }));
};
Object.defineProperty(URLSearchParams.prototype, 'size', {
    get: function() { return this._params.length; },
    enumerable: true,
    configurable: true
});
if (typeof Symbol !== 'undefined' && Symbol.iterator) {
    URLSearchParams.prototype[Symbol.iterator] = URLSearchParams.prototype.entries;
}

function URL(url, base) {
    if (!(this instanceof URL)) {
        throw new TypeError('Failed to construct \'URL\': Please use the \'new\' operator.');
    }
    var baseRecord = null;
    if (base !== undefined && base !== null) {
        baseRecord = urlParse(base, null);
        if (!baseRecord) throw new TypeError('Failed to construct \'URL\': Invalid base URL');
    }
    var record = urlParse(url, baseRecord);
    if (!record) throw new TypeError('Failed to construct \'URL\': Invalid URL');
    this._record = record;
    this._searchParams = null;
}

// URL.parse / URL.canParse — the non-throwing spellings, which pages feature-detect before use.
URL.parse = function(url, base) {
    try {
        return new URL(url, base);
    } catch (e) {
        return null;
    }
};
URL.canParse = function(url, base) {
    return URL.parse(url, base) !== null;
};

function urlDefineAccessor(name, get, set) {
    Object.defineProperty(URL.prototype, name, { get: get, set: set, enumerable: true, configurable: true });
}

// Re-seed the cached searchParams from the query the URL now carries, keeping object identity.
function urlResyncSearchParams(url) {
    if (!url._searchParams) return;
    URLSearchParams.call(url._searchParams, url.search);
}

urlDefineAccessor('href',
    function() { return urlSerialize(this._record); },
    function(value) {
        var parsed = urlParse(value, null);
        if (!parsed) throw new TypeError('Failed to set the \'href\' property on \'URL\': Invalid URL');
        this._record = parsed;
        urlResyncSearchParams(this);
    });

urlDefineAccessor('origin', function() {
    var r = this._record;
    // A blob: URL's origin is the origin of the URL it wraps.
    if (r.scheme === 'blob:') {
        var inner = URL.parse(r.path);
        return inner ? inner.origin : 'null';
    }
    // A file: URL's origin is opaque, like an opaque path's and a non-special scheme's.
    if (r.opaque || r.host === null || r.scheme === 'file:' || !urlIsSpecial(r.scheme)) return 'null';
    return r.scheme + '//' + r.host + (r.port !== '' ? ':' + r.port : '');
});

urlDefineAccessor('protocol',
    function() { return this._record.scheme; },
    function(value) {
        var m = /^([A-Za-z][A-Za-z0-9+\-.]*):?$/.exec(String(value));
        if (m) this._record.scheme = m[1].toLowerCase() + ':';
    });

urlDefineAccessor('username',
    function() { return this._record.username; },
    function(value) { if (this._record.host !== null) this._record.username = String(value); });

urlDefineAccessor('password',
    function() { return this._record.password; },
    function(value) { if (this._record.host !== null) this._record.password = String(value); });

urlDefineAccessor('host',
    function() {
        var r = this._record;
        return r.host === null ? '' : r.host + (r.port !== '' ? ':' + r.port : '');
    },
    function(value) {
        var r = this._record;
        if (r.opaque) return;
        var s = String(value);
        var hostEnd = s.charAt(0) === '[' ? s.indexOf(']') + 1 : 0;
        var colon = s.indexOf(':', hostEnd);
        var host = colon === -1 ? s : s.substring(0, colon);
        var port = colon === -1 ? '' : s.substring(colon + 1);
        if (host === '' && urlIsSpecial(r.scheme) && r.scheme !== 'file:') return;
        if (!urlIsValidPort(port, r.scheme)) return;
        r.host = host.toLowerCase();
        r.port = urlNormalizePort(port, r.scheme);
    });

urlDefineAccessor('hostname',
    function() { return this._record.host === null ? '' : this._record.host; },
    function(value) {
        var r = this._record;
        if (r.opaque) return;
        var host = String(value);
        if (host === '' && urlIsSpecial(r.scheme) && r.scheme !== 'file:') return;
        r.host = host.toLowerCase();
    });

urlDefineAccessor('port',
    function() { return this._record.port; },
    function(value) {
        var r = this._record;
        if (r.opaque || r.host === null) return;
        var port = String(value);
        if (!urlIsValidPort(port, r.scheme)) return;
        r.port = urlNormalizePort(port, r.scheme);
    });

urlDefineAccessor('pathname',
    function() { return this._record.path; },
    function(value) {
        var r = this._record;
        if (r.opaque) return;
        var path = String(value);
        if (urlIsSpecial(r.scheme)) path = path.replace(/\\/g, '/');
        if (r.host !== null && path.charAt(0) !== '/') path = '/' + path;
        r.path = urlPercentEncode(urlRemoveDotSegments(path), URL_PATH_UNSAFE);
    });

urlDefineAccessor('search',
    function() {
        var q = this._record.query;
        return q === null || q === '' ? '' : '?' + q;
    },
    function(value) {
        var s = String(value);
        if (s === '') {
            this._record.query = null;
        } else {
            if (s.charAt(0) === '?') s = s.substring(1);
            this._record.query = urlPercentEncode(s, URL_QUERY_UNSAFE);
        }
        urlResyncSearchParams(this);
    });

urlDefineAccessor('hash',
    function() {
        var f = this._record.fragment;
        return f === null || f === '' ? '' : '#' + f;
    },
    function(value) {
        var s = String(value);
        if (s === '') { this._record.fragment = null; return; }
        if (s.charAt(0) === '#') s = s.substring(1);
        this._record.fragment = urlPercentEncode(s, URL_FRAGMENT_UNSAFE);
    });

urlDefineAccessor('searchParams', function() {
    if (!this._searchParams) {
        this._searchParams = new URLSearchParams(this.search);
        this._searchParams._url = this;
    }
    return this._searchParams;
});

URL.prototype.toString = function() { return this.href; };
URL.prototype.toJSON = function() { return this.href; };
