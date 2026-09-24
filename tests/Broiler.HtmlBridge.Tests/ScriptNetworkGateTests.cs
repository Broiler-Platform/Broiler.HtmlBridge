using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;
using Broiler.Net.Http;
using Reply = Broiler.HtmlBridge.Tests.LoopbackCookieServer.Reply;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The script-facing gates on the profile network: what page script can make the profile's
/// transport send, what it can read back, and what <c>document.cookie</c> gives it — through the
/// real bridge, against a loopback server.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why these gates ship with the shared jar.</b> Once the bridge's loaders send the profile's
/// cookies, a <c>fetch()</c> that ignored its credentials mode, copied Set-Cookie into its response or
/// let script set a Cookie header would hand page script the profile itself. Each test pins one gate
/// with a request the server logs and a result the page writes into <c>#out</c>.
/// </para>
/// <para>
/// <b>Sites.</b> The page is on <c>127.0.0.1</c> unless a test says otherwise; <c>localhost</c> on the
/// same port is another origin and another site (see <see cref="LoopbackCookieServer"/>). A cookie a
/// cross-site request may carry has to be <c>SameSite=None; Secure</c>, which loopback hosts accept
/// over plain HTTP as potentially trustworthy origins.
/// </para>
/// </remarks>
public class ScriptNetworkGateTests
{
    private const string Text = "text/plain";

    private static BrowserNetworkSession NewProfile(bool cookiesEnabled = true) =>
        new(new BrowserNetworkSessionOptions { Timeout = TimeSpan.FromSeconds(20), CookiesEnabled = cookiesEnabled });

    /// <summary>
    /// A server whose <c>/login</c> sets <c>sid=abc</c> for <c>127.0.0.1</c>, and whose
    /// <c>/login-local</c> sets <c>none=1</c> (SameSite=None) and <c>lax=1</c> for <c>localhost</c>.
    /// </summary>
    private static LoopbackCookieServer NewServer() =>
        new LoopbackCookieServer()
            .Map("/login", new Reply(Body: "<p>in</p>", SetCookies: ["sid=abc; Path=/"]))
            .Map("/login-local", new Reply(Body: "<p>in</p>",
                SetCookies: ["none=1; Path=/; SameSite=None; Secure", "lax=1; Path=/; SameSite=Lax"]));

    /// <summary>Navigates the profile to <paramref name="url"/> as browser UI does.</summary>
    private static void Navigate(BrowserNetworkSession profile, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = profile.Send(request, RequestContext.TopLevelNavigation(initiator: null));
        Assert.Equal(200, response.StatusCode);
    }

    /// <summary>A profile that has visited both sites' login pages.</summary>
    private static BrowserNetworkSession LoggedIn(LoopbackCookieServer server)
    {
        var profile = NewProfile();
        Navigate(profile, server.Url("/login"));
        Navigate(profile, server.LocalhostUrl("/login-local"));
        return profile;
    }

    private static bool Stored(BrowserNetworkSession profile, string name) =>
        profile.Cookies.Snapshot().Any(cookie => cookie.Name == name);

    private static ScriptEngine EngineFor(BrowserNetworkSession profile) =>
        new(new DomBridgeFactory(new DomBridgeSessionOptions { Network = profile, Cookies = profile }));

    private static string Page(string body = "") =>
        $"<!DOCTYPE html><html><head></head><body><div id=\"out\"></div>{body}</body></html>";

    /// <summary>
    /// Runs <paramref name="script"/> on a page at <paramref name="pageUrl"/> with the profile network
    /// and answers what it wrote to <c>#out</c>. The script calls <c>done(value)</c> when it has an
    /// answer — in a promise reaction, typically, which the engine drains before it serializes.
    /// </summary>
    private static string Run(BrowserNetworkSession profile, string pageUrl, string script, string body = "")
    {
        const string helpers = "function done(v) { document.getElementById('out').textContent = String(v); }";
        var rendered = EngineFor(profile).Execute([helpers, script], Page(body), pageUrl);
        Assert.NotNull(rendered);
        return PageProbe.OutOf(rendered!, decode: true);
    }

    /// <summary>A CORS reply for the page at <c>127.0.0.1</c>, credentialed or not.</summary>
    private static (string, string)[] Cors(LoopbackCookieServer server, bool credentials) =>
        credentials
            ? [("Access-Control-Allow-Origin", $"http://127.0.0.1:{server.Port}"), ("Access-Control-Allow-Credentials", "true")]
            : [("Access-Control-Allow-Origin", $"http://127.0.0.1:{server.Port}")];

    // ---------------------------------------------------------------------
    //  fetch(): credentials modes
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>credentials: 'omit'</c> on a same-origin request: no Cookie header goes out, and the
    /// response's Set-Cookie is not stored — the mode gates both directions.
    /// </summary>
    [Fact]
    public void FetchWithCredentialsOmitSendsAndStoresNothing()
    {
        using var server = NewServer()
            .Map("/data", new Reply(ContentType: Text, Body: "data", SetCookies: ["fromOmit=1; Path=/"]));
        using var profile = LoggedIn(server);

        var result = Run(profile, server.Url("/page"),
            "fetch('/data', { credentials: 'omit' }).then(function (r) { return r.text(); })" +
            ".then(function (t) { done(t); }, function (e) { done(e.name); });");

        Assert.Equal("data", result);
        Assert.Null(server.Single("/data").Cookie);
        Assert.False(Stored(profile, "fromOmit"));
    }

    /// <summary>
    /// fetch()'s default, <c>same-origin</c>: the page's own origin gets its cookie and may set one;
    /// another origin — here one that allows the read with <c>*</c> — gets none, although the profile
    /// holds a <c>SameSite=None</c> cookie for it, and its Set-Cookie is not stored.
    /// </summary>
    [Fact]
    public void FetchWithCredentialsSameOriginSendsCookiesOnlyToTheSameOrigin()
    {
        using var server = NewServer()
            .Map("/same", new Reply(ContentType: Text, Body: "same", SetCookies: ["fromSame=1; Path=/"]))
            .Map("/cross", new Reply(ContentType: Text, Body: "cross",
                SetCookies: ["fromCross=1; Path=/; SameSite=None; Secure"],
                Headers: [("Access-Control-Allow-Origin", "*")]));
        using var profile = LoggedIn(server);

        var result = Run(profile, server.Url("/page"),
            "var parts = [];" +
            "fetch('/same').then(function (r) { parts.push(r.type); })" +
            $".then(function () {{ return fetch('{server.LocalhostUrl("/cross")}'); }})" +
            ".then(function (r) { parts.push(r.type); return r.text(); })" +
            ".then(function (t) { parts.push(t); done(parts.join('|')); }, function (e) { done(e.name); });");

        Assert.Equal("basic|cors|cross", result);
        Assert.Equal("sid=abc", server.Single("/same").Cookie);
        Assert.True(Stored(profile, "fromSame"));
        Assert.Null(server.Single("/cross").Cookie);
        Assert.False(Stored(profile, "fromCross"));
    }

    /// <summary>
    /// <c>credentials: 'include'</c> to another origin sends that origin's cookie, and the page may read
    /// the response only if the server both names the page's origin and allows credentials; without
    /// <c>Access-Control-Allow-Credentials</c> the request still went out, but the page gets a
    /// <c>TypeError</c> and nothing of the response.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FetchWithCredentialsIncludeNeedsTheServersPermissionToBeRead(bool allowCredentials)
    {
        using var server = NewServer();
        server.Map("/api", new Reply(ContentType: Text, Body: "secret", Headers: Cors(server, allowCredentials)));
        using var profile = LoggedIn(server);

        var result = Run(profile, server.Url("/page"),
            $"fetch('{server.LocalhostUrl("/api")}', {{ credentials: 'include' }})" +
            ".then(function (r) { return r.text().then(function (t) { return r.type + ':' + t; }); })" +
            ".then(function (v) { done(v); }, function (e) { done(e.name); });");

        Assert.Equal(allowCredentials ? "cors:secret" : "TypeError", result);
        // Cross-site, so the SameSite=None cookie and not the Lax one.
        Assert.Equal("none=1", server.Single("/api").Cookie);
        Assert.Equal($"http://127.0.0.1:{server.Port}", server.Single("/api").Origin);
    }

    // ---------------------------------------------------------------------
    //  Request headers script may not set
    // ---------------------------------------------------------------------

    /// <summary>
    /// Forbidden request-headers are dropped from what script supplies — through fetch() and through
    /// XMLHttpRequest — while an ordinary author header goes through. The only Cookie header on the
    /// wire is the profile's.
    /// </summary>
    [Fact]
    public void ScriptSetCookieAndOtherForbiddenHeadersNeverReachTheWire()
    {
        using var server = NewServer()
            .Map("/echo-fetch", new Reply(ContentType: Text, Body: "f"))
            .Map("/echo-xhr", new Reply(ContentType: Text, Body: "x"));
        using var profile = LoggedIn(server);

        var result = Run(profile, server.Url("/page"),
            "fetch('/echo-fetch', { headers: { 'Cookie': 'forged=1', 'Cookie2': 'forged=2', 'Origin': 'http://evil.test'," +
            " 'Sec-Fetch-Site': 'none', 'Host': 'evil.test', 'X-Custom': 'kept' } })" +
            ".then(function () {" +
            "  var x = new XMLHttpRequest(); x.open('GET', '/echo-xhr');" +
            "  x.setRequestHeader('Cookie', 'forged=3'); x.setRequestHeader('X-Custom', 'xhr');" +
            "  x.setRequestHeader('X-Custom', 'again');" +
            "  x.onload = function () { done('xhr:' + x.status); }; x.onerror = function () { done('xhr-error'); };" +
            "  x.send();" +
            "}, function (e) { done(e.name); });");

        Assert.Equal("xhr:200", result);
        var fetched = server.Single("/echo-fetch");
        Assert.Equal("sid=abc", fetched.Cookie);
        Assert.Equal(1, fetched.CountOf("Cookie"));
        Assert.Null(fetched.Header("Cookie2"));
        Assert.Null(fetched.Origin);
        Assert.NotEqual("none", fetched.Header("Sec-Fetch-Site"));
        Assert.StartsWith("127.0.0.1:", fetched.Host);
        Assert.Equal("kept", fetched.Header("X-Custom"));

        var sent = server.Single("/echo-xhr");
        Assert.Equal("sid=abc", sent.Cookie);
        Assert.Equal(1, sent.CountOf("Cookie"));
        // XHR combines a repeated author header.
        Assert.Equal("xhr, again", sent.Header("X-Custom"));
    }

    /// <summary>
    /// A header name or value HTTP cannot carry is refused before anything is sent: fetch() rejects
    /// with a <c>TypeError</c> and <c>setRequestHeader</c> throws a <c>SyntaxError</c>, so a CR/LF in a
    /// value can never put an extra header line on the wire.
    /// </summary>
    [Fact]
    public void InvalidHeaderNamesAndValuesAreRefusedBeforeTheWire()
    {
        using var server = NewServer().Map("/never", new Reply(ContentType: Text, Body: "n"));
        using var profile = LoggedIn(server);

        var result = Run(profile, server.Url("/page"),
            "var parts = [];" +
            "try { var x = new XMLHttpRequest(); x.open('GET', '/never'); x.setRequestHeader('X-Bad', 'a\\r\\nInjected: 1'); parts.push('set'); }" +
            " catch (e) { parts.push(e.name); }" +
            "try { var y = new XMLHttpRequest(); y.open('GET', '/never'); y.setRequestHeader('Bad Name', 'v'); parts.push('set'); }" +
            " catch (e) { parts.push(e.name); }" +
            "fetch('/never', { headers: { 'X-Bad': 'a\\nInjected: 1' } })" +
            ".then(function () { parts.push('sent'); }, function (e) { parts.push(e.name); })" +
            ".then(function () { return fetch('/never', { method: 'TRACE' }); })" +
            ".then(function () { parts.push('sent'); }, function (e) { parts.push(e.name); })" +
            ".then(function () { return fetch('/never', { mode: 'navigate' }); })" +
            ".then(function () { parts.push('sent'); }, function (e) { parts.push(e.name); })" +
            ".then(function () { done(parts.join('|')); });");

        Assert.Equal("SyntaxError|SyntaxError|TypeError|TypeError|TypeError", result);
        Assert.Empty(server.RequestsFor("/never"));
    }

    // ---------------------------------------------------------------------
    //  What script may read back
    // ---------------------------------------------------------------------

    /// <summary>
    /// Set-Cookie and Set-Cookie2 never reach script: not through <c>headers.get</c>, <c>has</c>,
    /// <c>forEach</c> or the Headers object's own properties, and not through XHR's
    /// <c>getResponseHeader</c> or <c>getAllResponseHeaders</c>. The ordinary header beside them does,
    /// and the profile still stored the cookie — the transport saw it, the page did not.
    /// </summary>
    [Fact]
    public void SetCookieIsInvisibleToScriptThroughFetchAndXhr()
    {
        using var server = NewServer()
            .Map("/headers", new Reply(ContentType: Text, Body: "h",
                SetCookies: ["fromHeaders=1; Path=/"],
                Headers: [("Set-Cookie2", "legacy=1"), ("X-Visible", "yes")]));
        using var profile = LoggedIn(server);

        var result = Run(profile, server.Url("/page"),
            "var parts = [];" +
            "fetch('/headers').then(function (r) {" +
            "  var names = []; r.headers.forEach(function (v, n) { names.push(n.toLowerCase()); });" +
            "  parts.push(r.headers.get('set-cookie'), r.headers.get('Set-Cookie2'), r.headers.has('Set-Cookie')," +
            "    typeof r.headers['set-cookie'], names.indexOf('set-cookie') < 0 && names.indexOf('set-cookie2') < 0," +
            "    r.headers.get('x-visible'));" +
            "  var x = new XMLHttpRequest(); x.open('GET', '/headers');" +
            "  x.onload = function () {" +
            "    var all = x.getAllResponseHeaders().toLowerCase();" +
            "    parts.push(x.getResponseHeader('Set-Cookie'), x.getResponseHeader('set-cookie2'), all.indexOf('set-cookie') < 0," +
            "      x.getResponseHeader('X-Visible'));" +
            "    done(parts.join('|'));" +
            "  };" +
            "  x.onerror = function () { done('xhr-error'); };" +
            "  x.send();" +
            "}, function (e) { done(e.name); });");

        Assert.Equal("||false|undefined|true|yes|||true|yes", result);
        Assert.True(Stored(profile, "fromHeaders"));
    }

    /// <summary>
    /// A <c>no-cors</c> request to another origin is sent, cookies and all, but answers an opaque
    /// response: status 0, no headers, an empty body and an empty URL, whatever the server said — even
    /// with <c>Access-Control-Allow-Origin: *</c>. It keeps only no-CORS-safelisted headers, and a method
    /// outside GET/HEAD/POST is a <c>TypeError</c> before anything is sent.
    /// </summary>
    [Fact]
    public void ANoCorsCrossOriginResponseIsOpaque()
    {
        using var server = NewServer()
            .Map("/secret", new Reply(ContentType: Text, Body: "secret",
                Headers: [("Access-Control-Allow-Origin", "*"), ("X-Secret", "1")]));
        using var profile = LoggedIn(server);
        var target = server.LocalhostUrl("/secret");

        var result = Run(profile, server.Url("/page"),
            "var parts = [];" +
            $"fetch('{target}', {{ mode: 'no-cors', credentials: 'include', headers: {{ 'X-Custom': 'dropped', 'Accept-Language': 'de' }} }})" +
            ".then(function (r) {" +
            "  var count = 0; r.headers.forEach(function () { count++; });" +
            "  parts.push(r.type, r.status, r.ok, count, '[' + r.url + ']', r.redirected);" +
            "  return r.text();" +
            "}).then(function (t) { parts.push('[' + t + ']'); })" +
            $".then(function () {{ return fetch('{target}', {{ mode: 'no-cors', method: 'PUT' }}); }})" +
            ".then(function () { parts.push('put-sent'); }, function (e) { parts.push(e.name); })" +
            ".then(function () { done(parts.join('|')); });");

        Assert.Equal("opaque|0|false|0|[]|false|[]|TypeError", result);
        var request = server.Single("/secret");
        Assert.Equal("GET", request.Method);
        Assert.Equal("none=1", request.Cookie);
        Assert.Null(request.Header("X-Custom"));
        Assert.Equal("de", request.Header("Accept-Language"));
    }

    /// <summary>
    /// The redirect modes: <c>follow</c> reports the final URL and <c>redirected</c>; <c>manual</c>
    /// answers an opaque-redirect response (status 0, the URL that redirected) and does not follow;
    /// <c>error</c> is a <c>TypeError</c>.
    /// </summary>
    [Fact]
    public void RedirectModesAndTheResponseUrl()
    {
        using var server = NewServer()
            .Map("/redir", new Reply(302, Location: "/final"))
            .Map("/final", new Reply(ContentType: Text, Body: "final"));
        using var profile = LoggedIn(server);

        var result = Run(profile, server.Url("/page"),
            "var parts = [];" +
            "fetch('/redir#frag').then(function (r) { parts.push(r.status, r.url, r.redirected, r.type); })" +
            ".then(function () { return fetch('/redir', { redirect: 'manual' }); })" +
            ".then(function (r) { parts.push(r.type, r.status, r.url, r.redirected); return r.text(); })" +
            ".then(function (t) { parts.push('[' + t + ']'); })" +
            ".then(function () { return fetch('/redir', { redirect: 'error' }); })" +
            ".then(function () { parts.push('followed'); }, function (e) { parts.push(e.name); })" +
            ".then(function () { done(parts.join('|')); });");

        Assert.Equal(
            $"200|{server.Url("/final")}|true|basic|opaqueredirect|0|{server.Url("/redir")}|false|[]|TypeError",
            result);
        Assert.Equal(3, server.RequestsFor("/redir").Length);
        Assert.Single(server.RequestsFor("/final"));
    }

    /// <summary>
    /// A cross-origin <c>application/json</c> POST is preflighted: an <c>OPTIONS</c> request without
    /// cookies announces the method and the header, and the POST follows only when the server allows
    /// both. Refused, the POST never leaves and the page gets a <c>TypeError</c>.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ACrossOriginJsonPostIsPreflighted(bool allowed)
    {
        using var server = NewServer();
        server.Map("/api", request => request.Method == "OPTIONS"
            ? new Reply(204, ContentType: Text, Headers: allowed
                ? [.. Cors(server, credentials: false), ("Access-Control-Allow-Methods", "POST"), ("Access-Control-Allow-Headers", "content-type")]
                : Cors(server, credentials: false))
            : new Reply(ContentType: Text, Body: "posted:" + request.Body, Headers: Cors(server, credentials: false)));
        using var profile = LoggedIn(server);

        var result = Run(profile, server.Url("/page"),
            $"fetch('{server.LocalhostUrl("/api")}', {{ method: 'POST', headers: {{ 'Content-Type': 'application/json' }}, body: '{{\"a\":1}}' }})" +
            ".then(function (r) { return r.text(); }).then(function (t) { done(t); }, function (e) { done(e.name); });");

        var requests = server.RequestsFor("/api");
        Assert.Equal("OPTIONS", requests[0].Method);
        Assert.Equal("POST", requests[0].Header("Access-Control-Request-Method"));
        Assert.Equal("content-type", requests[0].Header("Access-Control-Request-Headers"));
        Assert.Null(requests[0].Cookie);
        if (allowed)
        {
            Assert.Equal("posted:{\"a\":1}", result);
            Assert.Equal(2, requests.Length);
            Assert.Equal("POST", requests[1].Method);
            Assert.StartsWith("application/json", requests[1].Header("Content-Type"));
        }
        else
        {
            Assert.Equal("TypeError", result);
            Assert.Single(requests);
        }
    }

    /// <summary>
    /// A <c>Request</c> object reports the mode, credentials and redirect a fetch of it uses — the
    /// defaults, or what its init said — and refuses what Fetch refuses. Fetching it honours them:
    /// the <c>omit</c> Request goes out without the cookie an init-less fetch sends.
    /// </summary>
    [Fact]
    public void ARequestObjectReportsTheModesAFetchOfItUses()
    {
        using var server = NewServer().Map("/r", new Reply(ContentType: Text, Body: "r"));
        using var profile = LoggedIn(server);

        var result = Run(profile, server.Url("/page"),
            "var parts = [];" +
            "var plain = new Request('/r');" +
            "parts.push(plain.mode, plain.credentials, plain.redirect);" +
            "var omit = new Request('/r?omit', { credentials: 'omit', mode: 'same-origin', redirect: 'error', method: 'post', body: 'b' });" +
            "parts.push(omit.mode, omit.credentials, omit.redirect, omit.method);" +
            "try { new Request('/r', { mode: 'navigate' }); parts.push('built'); } catch (e) { parts.push(e.name); }" +
            "try { new Request('/r', { credentials: 'sometimes' }); parts.push('built'); } catch (e) { parts.push(e.name); }" +
            "try { new Request('/r', { mode: 'no-cors', method: 'DELETE' }); parts.push('built'); } catch (e) { parts.push(e.name); }" +
            "fetch(omit).then(function (r) { parts.push(r.status); return fetch(plain); })" +
            ".then(function (r) { parts.push(r.status); done(parts.join('|')); }, function (e) { done(e.name); });");

        Assert.Equal("cors|same-origin|follow|same-origin|omit|error|POST|TypeError|TypeError|TypeError|200|200", result);
        var requests = server.RequestsFor("/r");
        Assert.Equal(2, requests.Length);
        Assert.Equal("POST", requests[0].Method);
        Assert.Null(requests[0].Cookie);
        Assert.Equal("GET", requests[1].Method);
        Assert.Equal("sid=abc", requests[1].Cookie);
    }

    /// <summary>
    /// Without a profile the fallback client answers fetch(): it sends no cookies, and what it
    /// returns passes the same filter — no Set-Cookie for script — with the URL after redirects.
    /// </summary>
    [Fact]
    public void WithoutAProfileFetchStillFiltersWhatScriptSees()
    {
        using var server = NewServer()
            .Map("/start", new Reply(302, Location: "/landing"))
            .Map("/landing", new Reply(ContentType: Text, Body: "landed",
                SetCookies: ["fallback=1; Path=/"], Headers: [("X-Visible", "yes")]));

        var rendered = new ScriptEngine().Execute(
            ["fetch('/start').then(function (r) {" +
             "  document.getElementById('out').textContent = [r.url, r.redirected, r.headers.get('set-cookie'), r.headers.get('x-visible')].join('|');" +
             "});"],
            Page(),
            server.Url("/page"));

        Assert.Equal($"{server.Url("/landing")}|true||yes", PageProbe.OutOf(rendered!));
        Assert.Null(server.Single("/landing").Cookie);
    }

    // ---------------------------------------------------------------------
    //  XMLHttpRequest and sendBeacon
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>withCredentials</c> is XHR's credentials mode: a cross-origin XHR carries the other site's
    /// cookie only when it is set. A same-origin XHR carries the page's cookie either way, and its
    /// <c>responseURL</c> is the URL after redirects.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void XhrWithCredentialsDecidesWhetherACrossOriginRequestCarriesCookies(bool withCredentials)
    {
        using var server = NewServer();
        server
            .Map("/xhr", new Reply(ContentType: Text, Body: "cross", Headers: Cors(server, credentials: true)))
            .Map("/xredir", new Reply(302, Location: "/xsame"))
            .Map("/xsame", new Reply(ContentType: Text, Body: "same"));
        using var profile = LoggedIn(server);

        var result = Run(profile, server.Url("/page"),
            "var parts = [];" +
            $"var x = new XMLHttpRequest(); x.open('GET', '{server.LocalhostUrl("/xhr")}'); x.withCredentials = {(withCredentials ? "true" : "false")};" +
            "x.onload = function () {" +
            "  parts.push(x.status, x.responseText);" +
            "  var y = new XMLHttpRequest(); y.open('GET', '/xredir');" +
            "  y.onload = function () { parts.push(y.responseText, y.responseURL); done(parts.join('|')); };" +
            "  y.onerror = function () { done('same-error'); };" +
            "  y.send();" +
            "};" +
            "x.onerror = function () { done('cross-error'); };" +
            "x.send();");

        Assert.Equal($"200|cross|same|{server.Url("/xsame")}", result);
        Assert.Equal(withCredentials ? "none=1" : null, server.Single("/xhr").Cookie);
        Assert.Equal("sid=abc", server.Single("/xsame").Cookie);
    }

    /// <summary>
    /// A page that replaces <c>window.fetch</c> changes what its own <c>fetch()</c> calls do — the probe
    /// call shows the replacement took — and nothing else: XMLHttpRequest and <c>sendBeacon</c> still
    /// send through the native core, with the profile's cookie, and the replacement never sees them.
    /// </summary>
    [Fact]
    public void ReplacingWindowFetchInterceptsNeitherXhrNorSendBeacon()
    {
        using var server = NewServer()
            .Map("/xhr-target", new Reply(ContentType: Text, Body: "xhr"))
            .Map("/beacon", new Reply(204, ContentType: Text));
        using var profile = LoggedIn(server);

        var result = Run(profile, server.Url("/page"),
            "var seen = 0;" +
            "window.fetch = function () { seen++; return Promise.reject(new Error('replaced')); };" +
            "fetch('/probe').catch(function () {});" +
            "var beacon = navigator.sendBeacon('/beacon', 'payload');" +
            "var x = new XMLHttpRequest(); x.open('GET', '/xhr-target');" +
            "x.onload = function () { done(seen + '|' + beacon + '|' + x.status + ':' + x.responseText); };" +
            "x.onerror = function () { done(seen + '|' + beacon + '|xhr-error'); };" +
            "x.send();");

        Assert.Equal("1|true|200:xhr", result);
        Assert.Empty(server.RequestsFor("/probe"));
        Assert.Equal("sid=abc", server.Single("/xhr-target").Cookie);
        var beaconRequest = server.Single("/beacon");
        Assert.Equal("POST", beaconRequest.Method);
        Assert.Equal("payload", beaconRequest.Body);
        Assert.Equal("text/plain;charset=UTF-8", beaconRequest.Header("Content-Type"));
        Assert.Equal("sid=abc", beaconRequest.Cookie);
    }

    /// <summary>
    /// A beacon is credentialed (<c>include</c>), so a cross-site one carries the target's
    /// <c>SameSite=None</c> cookie. Its mode follows its body: plain text is a <c>no-cors</c> POST sent
    /// without a preflight; a Blob typed <c>application/json</c> is a <c>cors</c> request, preflighted
    /// first.
    /// </summary>
    [Fact]
    public void ABeaconIsACredentialedPostWhoseModeFollowsItsBody()
    {
        using var server = NewServer();
        server
            .Map("/b-text", new Reply(204, ContentType: Text))
            .Map("/b-json", request => request.Method == "OPTIONS"
                ? new Reply(204, ContentType: Text, Headers:
                    [.. Cors(server, credentials: true), ("Access-Control-Allow-Methods", "POST"), ("Access-Control-Allow-Headers", "content-type")])
                : new Reply(204, ContentType: Text, Headers: Cors(server, credentials: true)));
        using var profile = LoggedIn(server);

        var result = Run(profile, server.Url("/page"),
            $"var a = navigator.sendBeacon('{server.LocalhostUrl("/b-text")}', 'hello');" +
            $"var b = navigator.sendBeacon('{server.LocalhostUrl("/b-json")}', new Blob(['{{\"a\":1}}'], {{ type: 'application/json' }}));" +
            "var c; try { navigator.sendBeacon('ftp://example.test/x', 'x'); c = 'queued'; } catch (e) { c = e.name; }" +
            "done(a + '|' + b + '|' + c);");

        Assert.Equal("true|true|TypeError", result);

        var text = server.Single("/b-text");
        Assert.Equal("POST", text.Method);
        Assert.Equal("hello", text.Body);
        Assert.Equal("none=1", text.Cookie);

        var json = server.RequestsFor("/b-json");
        Assert.Equal(2, json.Length);
        Assert.Equal("OPTIONS", json[0].Method);
        Assert.Equal("content-type", json[0].Header("Access-Control-Request-Headers"));
        Assert.Equal("POST", json[1].Method);
        Assert.Equal("{\"a\":1}", json[1].Body);
        Assert.Equal("application/json", json[1].Header("Content-Type"));
        Assert.Equal("none=1", json[1].Cookie);
    }

    // ---------------------------------------------------------------------
    //  document.cookie
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>document.cookie</c> is the profile's store: it reads the cookie navigation set, a cookie it
    /// writes goes out on the next HTTP request, and an HttpOnly cookie can be neither read nor
    /// overwritten from script — while HTTP still sends it unchanged.
    /// </summary>
    [Fact]
    public void DocumentCookieRoundTripsWithHttpCookiesButNeverTouchesHttpOnly()
    {
        using var server = NewServer()
            .Map("/login-http-only", new Reply(Body: "<p>in</p>", SetCookies: ["h=secret; Path=/; HttpOnly"]))
            .Map("/echo", new Reply(ContentType: Text, Body: "e"));
        using var profile = LoggedIn(server);
        Navigate(profile, server.Url("/login-http-only"));

        var result = Run(profile, server.Url("/page"),
            "var before = document.cookie;" +
            "document.cookie = 'js=1; path=/';" +
            "document.cookie = 'h=overwritten; path=/';" +
            "var after = document.cookie;" +
            "fetch('/echo').then(function () { done(before + '|' + after); }, function (e) { done(e.name); });");

        Assert.Equal("sid=abc|sid=abc; js=1", result);
        var echoed = server.Single("/echo").Cookie!;
        Assert.Contains("js=1", echoed);
        Assert.Contains("h=secret", echoed);
        Assert.DoesNotContain("overwritten", echoed);
        var httpOnly = Assert.Single(profile.Cookies.Snapshot(), cookie => cookie.Name == "h");
        Assert.Equal("secret", httpOnly.Value);
        Assert.True(httpOnly.HttpOnly);
    }

    /// <summary><c>navigator.cookieEnabled</c> reflects the profile's cookie setting.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NavigatorCookieEnabledReflectsTheProfile(bool enabled)
    {
        using var server = NewServer();
        using var profile = NewProfile(enabled);

        var result = Run(profile, server.Url("/page"),
            "document.cookie = 'js=1; path=/'; done(navigator.cookieEnabled + '|[' + document.cookie + ']');");

        Assert.Equal(enabled ? "true|[js=1]" : "false|[]", result);
        Assert.Equal(enabled, Stored(profile, "js"));
    }

    /// <summary>
    /// Without a profile, <c>document.cookie</c> is still a real cookie store — attributes parsed,
    /// deletion honoured — but one private to the bridge, never shared with another.
    /// </summary>
    [Fact]
    public void WithoutAProfileDocumentCookieIsAPrivateRealStore()
    {
        const string script =
            "document.cookie = 'a=1; Path=/'; document.cookie = 'b=2; Path=/'; document.cookie = 'a=; Max-Age=0; Path=/';" +
            "document.getElementById('out').textContent = '[' + document.cookie + ']';";

        var first = PageProbe.OutOf(new ScriptEngine().Execute([script], Page(), "http://127.0.0.1/page")!);
        var second = PageProbe.OutOf(new ScriptEngine().Execute(
            ["document.getElementById('out').textContent = '[' + document.cookie + ']';"], Page(), "http://127.0.0.1/page")!);

        Assert.Equal("[b=2]", first);
        Assert.Equal("[]", second);
    }

    /// <summary>
    /// A document whose URL is not HTTP(S) is cookie-averse: <c>about:blank</c> (attached with no URL)
    /// and a <c>data:</c> document read the empty string and write nothing, even with a profile.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("data:text/html,hello")]
    public void CookieAverseTopDocumentsReadAndWriteNothing(string? pageUrl)
    {
        using var profile = NewProfile();

        var rendered = EngineFor(profile).Execute(
            ["document.cookie = 'a=1; path=/'; document.getElementById('out').textContent = '[' + document.cookie + ']';"],
            Page(),
            pageUrl);

        Assert.Equal("[]", PageProbe.OutOf(rendered!));
        Assert.Empty(profile.Cookies.Snapshot());
    }

    /// <summary>
    /// A frame's <c>document.cookie</c> is its own document's: a same-origin frame at another path
    /// sees a cookie scoped to that path, which the page does not, and a cookie the frame writes lands
    /// in the profile under the frame's URL.
    /// </summary>
    [Fact]
    public void AFramesDocumentCookieIsItsOwnDocuments()
    {
        using var server = NewServer()
            .Map("/sub/frame", new Reply(Body: "<html><body><script>document.cookie = 'fromFrameJs=1';</script></body></html>",
                SetCookies: ["scoped=1; Path=/sub"]));
        using var profile = LoggedIn(server);

        var result = Run(profile, server.Url("/page"),
            "var frame = document.getElementById('f').contentDocument.cookie;" +
            "done(document.cookie + '|' + frame);",
            "<iframe id=\"f\" src=\"/sub/frame\"></iframe>");

        // Longest path first: the frame URL /sub/frame gives fromFrameJs the default path /sub.
        Assert.Equal("sid=abc|scoped=1; fromFrameJs=1; sid=abc", result);
        var written = Assert.Single(profile.Cookies.Snapshot(), cookie => cookie.Name == "fromFrameJs");
        Assert.Equal("/sub", written.Path);
    }

    /// <summary>
    /// A cross-site frame — <c>127.0.0.1</c> inside a <c>localhost</c> page — reads only the cookies a
    /// third-party context may: its <c>SameSite=None</c> cookie, not its Lax one. Its own
    /// <c>fetch()</c> is its request, not the page's: sent to the page's site it carries only that
    /// site's <c>SameSite=None</c> cookie, where the page's own request would carry the Lax one too.
    /// The same frame served same-site is the control.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ACrossSiteFrameGetsNoLaxCookiesThroughDocumentCookieOrFetch(bool crossSite)
    {
        using var server = NewServer()
            .Map("/login-ip", new Reply(Body: "<p>in</p>",
                SetCookies: ["iplax=1; Path=/; SameSite=Lax", "ipnone=1; Path=/; SameSite=None; Secure"]));
        server
            .Map("/xframe", new Reply(Body:
                "<html><body><script>" +
                $"fetch('{server.Url("/report")}?c=' + encodeURIComponent(document.cookie));" +
                $"fetch('{server.LocalhostUrl("/xfetch")}', {{ credentials: 'include' }});" +
                "</script></body></html>"))
            .Map("/report", new Reply(ContentType: Text, Body: "r"))
            .Map("/xfetch", new Reply(ContentType: Text, Body: "x", Headers: Cors(server, credentials: true)));
        using var profile = NewProfile();
        Navigate(profile, server.Url("/login-ip"));
        Navigate(profile, server.LocalhostUrl("/login-local"));

        var pageUrl = crossSite ? server.LocalhostUrl("/page") : server.Url("/page");
        var result = Run(profile, pageUrl,
            "done(typeof document.getElementById('f').contentWindow);",
            $"<iframe id=\"f\" src=\"{server.Url("/xframe")}\"></iframe>");

        Assert.Equal("object", result);
        var reported = Uri.UnescapeDataString(server.Single("/report").Path.Split("?c=", 2)[1]);
        Assert.Equal(crossSite ? "ipnone=1" : "iplax=1; ipnone=1", reported);
        // From the frame at 127.0.0.1 to localhost is cross-site either way; had the request been
        // attributed to a localhost page, it would have been same-site and carried lax=1.
        Assert.Equal("none=1", server.Single("/xfetch").Cookie);
    }

    /// <summary>
    /// A frame sandboxed without <c>allow-same-origin</c> has an opaque origin, and its
    /// <c>document.cookie</c> throws <c>SecurityError</c> for both reading and writing; nothing it
    /// tried to write is stored.
    /// </summary>
    [Fact]
    public void ASandboxedFramesDocumentCookieThrowsSecurityError()
    {
        using var server = NewServer();
        server
            .Map("/sframe", new Reply(Body:
                "<html><body><script>" +
                "var r, w; try { r = 'read:' + document.cookie; } catch (e) { r = e.name; }" +
                "try { document.cookie = 'sandboxed=1'; w = 'wrote'; } catch (e) { w = e.name; }" +
                $"fetch('{server.Url("/report")}?c=' + encodeURIComponent(r + '|' + w), {{ mode: 'no-cors' }});" +
                "</script></body></html>"))
            .Map("/report", new Reply(ContentType: Text, Body: "r"));
        using var profile = LoggedIn(server);

        Run(profile, server.Url("/page"),
            "done(typeof document.getElementById('f').contentWindow);",
            "<iframe id=\"f\" sandbox=\"allow-scripts\" src=\"/sframe\"></iframe>");

        var reported = Uri.UnescapeDataString(server.Single("/report").Path.Split("?c=", 2)[1]);
        Assert.Equal("SecurityError|SecurityError", reported);
        Assert.False(Stored(profile, "sandboxed"));
    }

    // ---------------------------------------------------------------------
    //  Navigation initiator
    // ---------------------------------------------------------------------

    /// <summary>
    /// A script navigation names the document that started it, as the context the host attached the
    /// bridge with — or, when a frame's script navigates the top window, the frame's context — so the
    /// host can decide SameSite for the navigation.
    /// </summary>
    /// <remarks>
    /// The frame reaches the top window's <c>Location</c> through a reference the page left it rather
    /// than as <c>parent.location</c>: every document shares one realm, and while a frame's script
    /// runs the window-context switch rebinds the global <c>location</c> (and <c>top</c> is that
    /// global), so <c>parent.location</c> there is the frame's own. What is under test is whose script
    /// is running when the top Location is told to navigate.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AScriptNavigationNamesTheDocumentThatStartedIt(bool fromFrame)
    {
        DocumentRequestContext? top = null;
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            DocumentContextFactory = url => top = DocumentRequestContext.CreateTopLevel(url),
        }));
        var script = fromFrame
            ? "var topLocation = location; document.getElementById('f').contentWindow;"
            : "location.href = 'https://other.test/next';";
        var body = fromFrame
            ? "<iframe id=\"f\" srcdoc=\"<script>topLocation.href = 'https://other.test/next';</script>\"></iframe>"
            : string.Empty;

        using var session = engine.ExecuteInteractive([script], [], Page(body), "https://example.test/page");
        var pending = session!.TakePendingNavigation();

        Assert.NotNull(pending);
        Assert.Equal("https://other.test/next", pending!.Url);
        Assert.NotNull(top);
        if (fromFrame)
        {
            Assert.NotNull(pending.Initiator);
            Assert.Same(top, pending.Initiator!.Parent);
        }
        else
        {
            Assert.Same(top, pending.Initiator);
        }
    }

    /// <summary>
    /// A meta refresh the host finds in markup names the document that declared it, through the
    /// overload that takes that document's context.
    /// </summary>
    [Fact]
    public void AMetaRefreshNamesItsDocumentWhenTheHostGivesIt()
    {
        var document = DocumentRequestContext.CreateTopLevel(new Uri("https://example.test/page"));

        var request = MetaRefreshDiscovery.Find(
            "<meta http-equiv=\"refresh\" content=\"0;url=/next\">", "https://example.test/page", document);

        Assert.NotNull(request);
        Assert.Equal("https://example.test/next", request!.Url);
        Assert.Same(document, request.Initiator);
    }
}
