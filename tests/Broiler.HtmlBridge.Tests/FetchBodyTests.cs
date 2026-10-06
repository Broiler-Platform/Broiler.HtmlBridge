using System.Text;

using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;
using Broiler.Net.Http;
using Reply = Broiler.HtmlBridge.Tests.LoopbackCookieServer.Reply;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// What a page's request bodies put on the wire and what its response bodies read back — through
/// <c>fetch()</c>, <c>new Request</c>, <c>new Response</c>, <c>XMLHttpRequest</c> and
/// <c>navigator.sendBeacon</c> — against a loopback server. Every expectation is Chromium's answer,
/// measured with the same scripts against a local echo server.
/// </summary>
/// <remarks>
/// Bodies used to be strings: a <c>Uint8Array</c> went out as <c>0,1,2,255</c> and an
/// <c>ArrayBuffer</c>, a <c>Blob</c> or a <c>FormData</c> as <c>[object …]</c>, all as
/// <c>text/plain</c>. reCAPTCHA's checkbox posts its verification as a <c>Uint8Array</c> under
/// <c>application/x-protobuffer</c>, so the request could not be parsed and no check came.
/// </remarks>
public class FetchBodyTests
{
    private const string TextPlain = "text/plain;charset=UTF-8";
    private const string FormUrlEncoded = "application/x-www-form-urlencoded;charset=UTF-8";

    private static LoopbackCookieServer NewServer() =>
        new LoopbackCookieServer()
            .Map("/sink", new Reply(ContentType: "text/plain", Body: "ok"))
            .Map("/bin", new Reply(ContentType: "application/octet-stream", BodyBytes: [.. Enumerable.Range(0, 256).Select(i => (byte)i)]))
            .Map("/latin1", new Reply(ContentType: "text/plain; charset=iso-8859-1", BodyBytes: [0x63, 0x61, 0x66, 0xE9]));

    /// <summary>
    /// Runs <paramref name="script"/> on a page of <paramref name="server"/>'s and answers what it passed
    /// to <c>done</c>. <c>hex(buffer)</c> spells a buffer's or a view's bytes.
    /// </summary>
    private static string Run(LoopbackCookieServer server, string script)
    {
        using var profile = new BrowserNetworkSession(new BrowserNetworkSessionOptions { Timeout = TimeSpan.FromSeconds(20) });
        const string helpers =
            "function done(v) { document.getElementById('out').textContent = String(v); }" +
            "function hex(buffer) { var bytes = new Uint8Array(buffer), out = '';" +
            " for (var i = 0; i < bytes.length; i++) out += ('0' + bytes[i].toString(16)).slice(-2); return out; }";
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions { Network = profile, Cookies = profile }));
        var rendered = engine.Execute([helpers, script],
            "<!DOCTYPE html><html><head></head><body><div id=\"out\"></div></body></html>", server.Url("/page"));
        Assert.NotNull(rendered);
        return PageProbe.OutOf(rendered!, decode: true);
    }

    /// <summary>Each request <c>/sink</c> received, as its body's hex and its <c>Content-Type</c>.</summary>
    private static (string Hex, string? ContentType)[] Sunk(LoopbackCookieServer server) =>
        [.. server.RequestsFor("/sink").Select(request => (Convert.ToHexStringLower(request.BodyBytes ?? []), request.Header("Content-Type")))];

    /// <summary>A script posting each of <paramref name="bodies"/> to <c>/sink</c> in turn, through <c>fetch</c>.</summary>
    private static string FetchEach(params string[] bodies) =>
        "var chain = Promise.resolve();" +
        string.Concat(bodies.Select(body =>
            $"chain = chain.then(function () {{ return fetch('/sink', {{ method: 'POST', body: {body} }}); }});")) +
        "chain.then(function () { done('sent'); }, function (e) { done('threw ' + e); });";

    // ---------------------------------------------------------------------
    //  Request bodies
    // ---------------------------------------------------------------------

    /// <summary>
    /// An <c>ArrayBuffer</c> and every view of one are their bytes — the view's span only, for a
    /// <c>subarray</c> or an offset <c>DataView</c> — and carry no <c>Content-Type</c>.
    /// </summary>
    [Fact]
    public void ABufferOrAViewOfOneIsSentAsItsBytes()
    {
        using var server = NewServer();

        var result = Run(server,
            "var nine = new Uint8Array([9, 8, 7, 6, 5, 4, 3, 2, 1]);" +
            FetchEach(
                "new Uint8Array([0, 1, 2, 255, 128, 10])",
                "new Uint8Array([0, 1, 2, 255, 128, 10]).buffer",
                "nine.subarray(2, 5)",
                "new DataView(nine.buffer, 1, 3)",
                "new Int16Array([1, -2])"));

        Assert.Equal("sent", result);
        Assert.Equal(
            [("000102ff800a", null), ("000102ff800a", null), ("070605", null), ("080706", null), ("0100feff", null)],
            Sunk(server));
    }

    /// <summary>
    /// reCAPTCHA's verification request: a <c>Uint8Array</c> under the page's own
    /// <c>Content-Type: application/x-protobuffer</c> goes out as those bytes with that type.
    /// </summary>
    [Fact]
    public void ABinaryProtobufPostKeepsItsBytesAndItsType()
    {
        using var server = NewServer();

        var result = Run(server,
            "fetch('/sink', { method: 'POST', headers: { 'Content-Type': 'application/x-protobuffer' }," +
            " body: new Uint8Array([8, 1, 18, 3, 97, 98, 99, 200]) })" +
            ".then(function (r) { return r.text(); }).then(function (t) { done(t); }, function (e) { done('threw ' + e); });");

        Assert.Equal("ok", result);
        Assert.Equal([("08011203616263c8", "application/x-protobuffer")], Sunk(server));
    }

    /// <summary>
    /// A <c>Content-Type</c> the page set goes out as written: without the space a parsed value is
    /// re-serialized with, and empty when the page set it empty — which reCAPTCHA does for a request
    /// with neither a protobuf nor a type of its own — rather than dropped.
    /// </summary>
    [Fact]
    public void APagesContentTypeIsSentAsWritten()
    {
        using var server = NewServer();

        var result = Run(server,
            "fetch('/sink', { method: 'POST', headers: { 'Content-Type': 'application/x-www-form-urlencoded;charset=utf-8' }, body: 'a=1' })" +
            ".then(function () { return fetch('/sink', { method: 'POST', headers: { 'Content-Type': '' }, body: 'abc' }); })" +
            ".then(function () { done('sent'); }, function (e) { done('threw ' + e); });");

        Assert.Equal("sent", result);
        Assert.Equal(
            [
                (Convert.ToHexStringLower(Encoding.ASCII.GetBytes("a=1")), "application/x-www-form-urlencoded;charset=utf-8"),
                ("616263", ""),
            ],
            Sunk(server));
    }

    /// <summary>
    /// The type a body implies when the page names none: a string is UTF-8 <c>text/plain</c>, a
    /// <c>URLSearchParams</c> form data, a <c>Blob</c> its own type (none for an untyped one), and
    /// anything else its string conversion as text.
    /// </summary>
    [Fact]
    public void EachKindOfBodyImpliesItsOwnContentType()
    {
        using var server = NewServer();

        var result = Run(server, FetchEach(
            "'h\\u00e9llo'",
            "new URLSearchParams('a=1&b=x y')",
            "new Blob([new Uint8Array([1, 2]), 'z'], { type: 'application/x-foo' })",
            "new Blob(['abc'])",
            "42"));

        Assert.Equal("sent", result);
        Assert.Equal(
            [
                ("68c3a96c6c6f", TextPlain),
                (Convert.ToHexStringLower(Encoding.ASCII.GetBytes("a=1&b=x+y")), FormUrlEncoded),
                ("01027a", "application/x-foo"),
                ("616263", null),
                ("3432", TextPlain),
            ],
            Sunk(server));
    }

    /// <summary>
    /// A <c>FormData</c> is <c>multipart/form-data</c> as Chromium frames it: a
    /// <c>----WebKitFormBoundary</c> boundary, line breaks in names and string values made CRLF, a
    /// name's and a file name's quote, CR and LF percent-encoded, and a file's part carrying its name and
    /// type — <c>blob</c> and <c>application/octet-stream</c> for an untyped blob.
    /// </summary>
    [Fact]
    public void AFormDataIsSentAsMultipartFormData()
    {
        using var server = NewServer();

        var result = Run(server,
            "var fd = new FormData();" +
            "fd.append('t', 'a\\nb\\rc\\r\\nd');" +
            "fd.append('n\"a\\nme', 'v');" +
            "fd.append('f', new Blob([new Uint8Array([0, 255])], { type: 'image/x-test' }), 'q\"x\\ny.bin');" +
            "fd.append('g', new Blob(['z']));" +
            "fd.append('h', new File(['w'], ''));" +
            FetchEach("fd"));

        Assert.Equal("sent", result);
        var request = server.Single("/sink");
        var match = System.Text.RegularExpressions.Regex.Match(request.Header("Content-Type") ?? string.Empty, "^multipart/form-data; boundary=(----WebKitFormBoundary[A-Za-z0-9]{16})$");
        Assert.True(match.Success, request.Header("Content-Type"));
        var b = match.Groups[1].Value;
        byte[] expected =
        [
            .. Encoding.ASCII.GetBytes($"--{b}\r\nContent-Disposition: form-data; name=\"t\"\r\n\r\na\r\nb\r\nc\r\nd\r\n"),
            .. Encoding.ASCII.GetBytes($"--{b}\r\nContent-Disposition: form-data; name=\"n%22a%0D%0Ame\"\r\n\r\nv\r\n"),
            .. Encoding.ASCII.GetBytes($"--{b}\r\nContent-Disposition: form-data; name=\"f\"; filename=\"q%22x%0Ay.bin\"\r\nContent-Type: image/x-test\r\n\r\n"),
            0x00, 0xFF,
            .. Encoding.ASCII.GetBytes("\r\n"),
            .. Encoding.ASCII.GetBytes($"--{b}\r\nContent-Disposition: form-data; name=\"g\"; filename=\"blob\"\r\nContent-Type: application/octet-stream\r\n\r\nz\r\n"),
            .. Encoding.ASCII.GetBytes($"--{b}\r\nContent-Disposition: form-data; name=\"h\"; filename=\"\"\r\nContent-Type: application/octet-stream\r\n\r\nw\r\n"),
            .. Encoding.ASCII.GetBytes($"--{b}--\r\n"),
        ];
        Assert.Equal(Convert.ToHexStringLower(expected), Convert.ToHexStringLower(request.BodyBytes ?? []));
    }

    /// <summary>
    /// A <c>Request</c> made with a binary body sends those bytes when fetched, and its headers carry
    /// the type the body implies — none for bytes, <c>text/plain</c> for a string.
    /// </summary>
    [Fact]
    public void ARequestKeepsItsBinaryBodyAndItsImpliedType()
    {
        using var server = NewServer();

        var result = Run(server,
            "var types = [String(new Request('/x', { method: 'POST', body: 'q' }).headers.get('content-type'))," +
            " String(new Request('/x', { method: 'POST', body: new Uint8Array([1]) }).headers.get('content-type'))," +
            " String(new Request('/x', { method: 'POST', body: new Blob(['x'], { type: 'a/b' }), headers: { 'content-type': 'c/d' } }).headers.get('content-type'))];" +
            "fetch(new Request('/sink', { method: 'POST', body: new Uint8Array([7, 7, 255]) }))" +
            ".then(function () { done(types.join('|')); }, function (e) { done('threw ' + e); });");

        Assert.Equal($"{TextPlain}|null|c/d", result);
        Assert.Equal([("0707ff", null)], Sunk(server));
    }

    /// <summary>
    /// <c>XMLHttpRequest.send</c> sends what fetch sends — bytes as bytes — and adds its own two rules:
    /// a document goes as its markup, <c>text/html;charset=UTF-8</c>, and a <c>charset</c> in the
    /// page's <c>Content-Type</c> becomes <c>UTF-8</c> for a string, a <c>URLSearchParams</c> or a
    /// document, but not for bytes or a blob. A <c>GET</c> sends no body.
    /// </summary>
    [Fact]
    public void AnXhrSendsBytesAsBytesAndRewritesATextBodysCharset()
    {
        using var server = NewServer();

        var result = Run(server,
            "function send(body, type, method) { return new Promise(function (resolve) {" +
            " var x = new XMLHttpRequest(); x.open(method || 'POST', '/sink');" +
            " if (type) x.setRequestHeader('Content-Type', type);" +
            " x.onload = function () { resolve(); }; x.onerror = function () { resolve(); }; x.send(body); }); }" +
            "var doc = document.implementation.createHTMLDocument('t');" +
            "var steps = [" +
            " function () { return send(new Uint8Array([0, 1, 2, 255])); }," +
            " function () { return send(new Uint8Array([65, 66]).buffer); }," +
            " function () { return send(new Blob(['x'], { type: 'a/b' })); }," +
            " function () { return send(new URLSearchParams('a=b')); }," +
            " function () { return send(doc); }," +
            " function () { return send('x', 'text/plain;charset=latin1'); }," +
            " function () { return send('{}', 'application/json; charset=iso-8859-1'); }," +
            " function () { return send(new Uint8Array([65]), 'text/plain;charset=latin1'); }," +
            " function () { return send(new Blob(['x']), 'text/plain;charset=latin1'); }," +
            " function () { return send('ignored', null, 'GET'); }];" +
            "var chain = Promise.resolve();" +
            "steps.forEach(function (step) { chain = chain.then(step); });" +
            "chain.then(function () { done('sent'); }, function (e) { done('threw ' + e); });");

        Assert.Equal("sent", result);
        Assert.Equal(
            [
                ("000102ff", null),
                ("4142", null),
                ("78", "a/b"),
                (Convert.ToHexStringLower(Encoding.ASCII.GetBytes("a=b")), FormUrlEncoded),
                (Convert.ToHexStringLower(Encoding.ASCII.GetBytes("<!DOCTYPE html><html><head><title>t</title></head><body></body></html>")), "text/html;charset=UTF-8"),
                ("78", TextPlain),
                ("7b7d", "application/json; charset=UTF-8"),
                ("41", "text/plain;charset=latin1"),
                ("78", "text/plain;charset=latin1"),
                ("", null),
            ],
            Sunk(server));
    }

    /// <summary>A beacon's bytes are its body as they are, with no <c>Content-Type</c>.</summary>
    [Fact]
    public void ABeaconSendsATypedArrayAsItsBytes()
    {
        using var server = NewServer();

        var result = Run(server,
            "done([navigator.sendBeacon('/sink', new Uint8Array([3, 4, 255])), navigator.sendBeacon('/sink', 'str')].join('|'));");

        Assert.Equal("true|true", result);
        Assert.Equal([("0304ff", null), ("737472", TextPlain)], Sunk(server));
    }

    // ---------------------------------------------------------------------
    //  Response bodies
    // ---------------------------------------------------------------------

    /// <summary>
    /// A binary response reads back as the bytes received — through <c>arrayBuffer()</c>, <c>blob()</c>
    /// and the <c>body</c> stream — while <c>text()</c> decodes UTF-8 whatever the charset: a byte that
    /// is not UTF-8 is U+FFFD, and so is <c>é</c> sent as ISO-8859-1. A blob's type is the MIME type
    /// without its parameters.
    /// </summary>
    [Fact]
    public void AResponseReadsBackTheBytesItReceived()
    {
        using var server = NewServer();

        var result = Run(server,
            "var out = [];" +
            "fetch('/bin').then(function (r) { return r.arrayBuffer(); })" +
            ".then(function (b) { out.push(b.byteLength + ':' + hex(b).slice(0, 8) + ':' + hex(b).slice(-8)); return fetch('/bin'); })" +
            ".then(function (r) { return r.blob(); })" +
            ".then(function (b) { out.push(b.size + ':' + b.type); return fetch('/bin'); })" +
            ".then(function (r) { return r.body.getReader().read(); })" +
            ".then(function (chunk) { out.push(chunk.value.length + ':' + hex(chunk.value).slice(-8)); return fetch('/bin'); })" +
            ".then(function (r) { return r.text(); })" +
            ".then(function (t) { out.push(t.length + ':' + t.charCodeAt(128)); return fetch('/latin1'); })" +
            ".then(function (r) { return r.text(); })" +
            ".then(function (t) { out.push(t.length + ':' + t.charCodeAt(3)); return fetch('/latin1'); })" +
            ".then(function (r) { return r.blob(); })" +
            ".then(function (b) { out.push(b.type); done(out.join('|')); }, function (e) { done('threw ' + e); });");

        Assert.Equal("256:00010203:fcfdfeff|256:application/octet-stream|256:fcfdfeff|256:65533|4:65533|text/plain", result);
    }

    /// <summary>
    /// <c>XMLHttpRequest</c> hands a binary response back as received for <c>arraybuffer</c> and
    /// <c>blob</c>, and decodes <c>responseText</c> by the response's charset — ISO-8859-1's <c>é</c>
    /// is U+00E9 there, where fetch's <c>text()</c> reads U+FFFD.
    /// </summary>
    [Fact]
    public void AnXhrResponseIsTheBytesReceivedAndItsTextFollowsTheCharset()
    {
        using var server = NewServer();

        var result = Run(server,
            "function get(url, type) { return new Promise(function (resolve) {" +
            " var x = new XMLHttpRequest(); x.open('GET', url); if (type) x.responseType = type;" +
            " x.onload = function () { resolve(x); }; x.send(); }); }" +
            "var out = [];" +
            "get('/bin', 'arraybuffer')" +
            ".then(function (x) { out.push(x.response.byteLength + ':' + hex(x.response).slice(-8)); return get('/bin', 'blob'); })" +
            ".then(function (x) { out.push(x.response.size); return get('/latin1'); })" +
            ".then(function (x) { out.push(x.responseText.length + ':' + x.responseText.charCodeAt(3)); done(out.join('|')); }," +
            " function (e) { done('threw ' + e); });");

        Assert.Equal("256:fcfdfeff|256|4:233", result);
    }

    /// <summary>
    /// <c>new Response</c> and <c>new Request</c> take a body as <c>fetch</c> does: bytes stay bytes,
    /// the body implies the <c>Content-Type</c>, and reading decodes UTF-8 with a byte-order mark
    /// dropped and a malformed byte U+FFFD.
    /// </summary>
    [Fact]
    public void ConstructedBodiesAreBytesAndDecodeAsUtf8()
    {
        using var server = NewServer();

        var result = Run(server,
            "var parts = [];" +
            "new Response(new Uint8Array([0, 255, 128])).arrayBuffer()" +
            ".then(function (b) { parts.push(hex(b)); return new Response(new Uint8Array([0xEF, 0xBB, 0xBF, 0x41])).text(); })" +
            ".then(function (t) { parts.push(t); return new Response(new Uint8Array([0xEF, 0xBB, 0xBF, 0x31])).json(); })" +
            ".then(function (j) {" +
            " parts.push(j);" +
            " parts.push(String(new Response('x').headers.get('content-type')));" +
            " parts.push(String(new Response(new Uint8Array([1])).headers.get('content-type')));" +
            " parts.push(String(new Response(new URLSearchParams('a=b')).headers.get('content-type')));" +
            " parts.push(String(new Response(new Blob(['x'], { type: 'a/b' })).headers.get('content-type')));" +
            " return new Request('/x', { method: 'POST', body: new Uint8Array([0x41, 0x80]) }).text(); })" +
            ".then(function (t) { parts.push(t.length + ':' + t.charCodeAt(1)); return new Request('/x', { method: 'POST', body: new Uint8Array([5, 250]) }).arrayBuffer(); })" +
            ".then(function (b) { parts.push(hex(b)); done(parts.join('|')); }, function (e) { done('threw ' + e); });");

        Assert.Equal($"00ff80|A|1|{TextPlain}|null|{FormUrlEncoded}|a/b|2:65533|05fa", result);
    }
}
