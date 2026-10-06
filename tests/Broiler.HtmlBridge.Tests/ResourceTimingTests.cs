using Broiler.HtmlBridge.Net;
using Broiler.Net.Http;
using Reply = Broiler.HtmlBridge.Tests.LoopbackCookieServer.Reply;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Resource Timing: a <c>PerformanceResourceTiming</c> entry for each fetch a document makes, and what
/// it shows. Every expectation is Chromium's, measured with the same requests against a server that
/// answers as this one does.
/// </summary>
/// <remarks>
/// <para>
/// The bridge recorded no entry at all: <c>getEntriesByType('resource')</c> was always empty, which no
/// browser answers, and reCAPTCHA looks its own scripts up there.
/// </para>
/// <para>
/// The page is on <c>localhost</c>; <c>127.0.0.1</c> is the same server and another origin. Times are
/// compared with each other rather than with numbers, since they are measured.
/// </para>
/// </remarks>
public class ResourceTimingTests
{
    private const string Js = "text/javascript";
    private const string Script = "window.loaded = (window.loaded || 0) + 1;";

    private static BrowserNetworkSession NewProfile() =>
        new(new BrowserNetworkSessionOptions { Timeout = TimeSpan.FromSeconds(20) });

    /// <summary>Runs <paramref name="script"/> on a page at <c>localhost</c>, settles it, and answers its <c>#out</c>.</summary>
    private static string Run(LoopbackCookieServer server, string script, string head = "", string body = "")
    {
        using var profile = NewProfile();
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions { Network = profile, Cookies = profile }));
        using var session = engine.ExecuteInteractive(
            [Helpers, script],
            [],
            $"<html><head>{head}</head><body><div id=\"out\">waiting</div>{body}</body></html>",
            server.LocalhostUrl("/page"));

        return PageProbe.OutOf(session!.SettleLoadWindow(), decode: true);
    }

    // done(v) writes the answer; entry(path) is the one resource entry whose URL ends in path; load(src)
    // inserts a script and calls back once it has run.
    private const string Helpers =
        "function done(v) { document.getElementById('out').textContent = String(v); }" +
        "function entries(path) { return performance.getEntriesByType('resource').filter(function (e) { return e.name.slice(-path.length) === path; }); }" +
        "function entry(path) { var all = entries(path); return all.length === 1 ? all[0] : null; }" +
        "function load(src, then, crossorigin) {" +
        "  var s = document.createElement('script'); s.src = src;" +
        "  if (crossorigin) s.setAttribute('crossorigin', crossorigin);" +
        "  s.onload = then; s.onerror = function () { done('error ' + src); };" +
        "  document.head.appendChild(s); }";

    // An entry's values, then whether its times are in order and where Chromium puts them.
    private const string Describe =
        "function describe(e) {" +
        "  return [e.entryType, e.initiatorType, e.renderBlockingStatus, e.nextHopProtocol, e.responseStatus, e.contentType," +
        "    e.encodedBodySize, e.decodedBodySize, e.transferSize, e.deliveryType, e.contentEncoding].join() + ' | ' +" +
        "    [e.workerStart, e.redirectStart, e.redirectEnd, e.secureConnectionStart, e.firstInterimResponseStart].join() + ' | ' +" +
        "    [e.startTime > 0, e.fetchStart === e.startTime, e.duration === e.responseEnd - e.startTime, e.responseEnd >= e.startTime].join(); }" +
        "function phases(e) {" +
        "  return [e.domainLookupStart === e.fetchStart, e.domainLookupEnd === e.fetchStart, e.connectStart === e.fetchStart," +
        "    e.connectEnd === e.fetchStart, e.requestStart === e.fetchStart, e.responseStart >= e.requestStart," +
        "    e.finalResponseHeadersStart === e.responseStart, e.responseEnd >= e.responseStart].join(); }";

    /// <summary>
    /// A script a page inserts has an entry once it has loaded: named by its URL, a <c>script</c> that
    /// did not hold up rendering, with its status, type, protocol and sizes -- the body's, and 300 more
    /// for what it was transferred in -- and its times in order, the connection's at the fetch's start.
    /// </summary>
    [Fact]
    public void AnInsertedScriptHasAnEntry()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/a.js", new Reply(ContentType: Js, Body: Script));

        Assert.Equal(
            "resource,script,non-blocking,http/1.1,200,text/javascript,41,41,341,, | 0,0,0,0,0 | true,true,true,true" +
            " / true,true,true,true,true,true,true,true / true,[object PerformanceResourceTiming],1",
            Run(server, Describe +
                "load('/a.js', function () { var e = entry('/a.js');" +
                "  done(describe(e) + ' / ' + phases(e) + ' / ' + (e instanceof PerformanceResourceTiming) + ',' +" +
                "    Object.prototype.toString.call(e) + ',' + performance.getEntriesByName(e.name).length); });"));
    }

    /// <summary>
    /// A response of another origin that sent no <c>Timing-Allow-Origin</c> shows its start and its end
    /// and nothing between: no phases, sizes or protocol. Not CORS-same-origin, it shows no status or
    /// type either.
    /// </summary>
    [Fact]
    public void AnotherOriginsResponseShowsOnlyItsStartAndEnd()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/x.js", new Reply(ContentType: Js, Body: Script));

        Assert.Equal(
            "resource,script,non-blocking,,0,,0,0,0,, | 0,0,0,0,0 | true,true,true,true" +
            " / 0,0,0,0,0,0,0,0",
            Run(server, Describe +
                $"load('{server.Url("/x.js")}', function () {{ var e = entry('/x.js');" +
                "  done(describe(e) + ' / ' + [e.domainLookupStart, e.domainLookupEnd, e.connectStart, e.connectEnd," +
                "    e.requestStart, e.responseStart, e.finalResponseHeadersStart, e.secureConnectionStart].join()); });"));
    }

    /// <summary>
    /// <c>Timing-Allow-Origin</c> -- <c>*</c>, or the page's origin -- shows another origin's timings,
    /// sizes and protocol; the status and type are still not the page's to read. A CORS response is the
    /// other way round: its status and type, and none of its timings.
    /// </summary>
    [Theory]
    [InlineData("*", null, "resource,script,non-blocking,http/1.1,0,,41,41,341,, | true,true,true,true,true,true,true,true")]
    [InlineData("origin", null, "resource,script,non-blocking,http/1.1,0,,41,41,341,, | true,true,true,true,true,true,true,true")]
    [InlineData(null, "anonymous", "resource,script,non-blocking,,200,text/javascript,0,0,0,, | false,false,false,false,false,true,true,true")]
    public void TimingAllowOriginShowsTimingsAndCorsShowsTheResponse(string? allow, string? crossorigin, string expected)
    {
        using var server = new LoopbackCookieServer();
        var page = server.LocalhostUrl("").TrimEnd('/');
        var headers = new List<(string, string)>();
        if (allow is not null)
            headers.Add(("Timing-Allow-Origin", allow == "origin" ? page : allow));
        if (crossorigin is not null)
            headers.Add(("Access-Control-Allow-Origin", page));
        server.Map("/x.js", new Reply(ContentType: Js, Body: Script, Headers: headers));

        Assert.Equal(expected, Run(server, Describe +
            $"load('{server.Url("/x.js")}', function () {{ var e = entry('/x.js');" +
            "  done([e.entryType, e.initiatorType, e.renderBlockingStatus, e.nextHopProtocol, e.responseStatus, e.contentType," +
            "    e.encodedBodySize, e.decodedBodySize, e.transferSize, e.deliveryType, e.contentEncoding].join() + ' | ' + phases(e)); }" +
            $"{(crossorigin is null ? "" : $", '{crossorigin}'")});"));
    }

    /// <summary>
    /// A redirected fetch is named by the URL first requested. Redirected within the page's origin, it
    /// shows when the redirect began -- the entry's start -- and ended, which is where the fetch of the
    /// final URL starts.
    /// </summary>
    [Fact]
    public void ARedirectedFetchIsNamedByItsFirstUrl()
    {
        using var server = new LoopbackCookieServer();
        server
            .Map("/hop.js", new Reply(302, Location: "/b.js"))
            .Map("/b.js", new Reply(ContentType: Js, Body: Script));

        Assert.Equal(
            "1,0,true,true,true,true,true",
            Run(server,
                "load('/hop.js', function () { var e = entry('/hop.js');" +
                "  done([entries('/hop.js').length, entries('/b.js').length, e.redirectStart === e.startTime, e.redirectEnd > 0," +
                "    e.fetchStart === e.redirectEnd, e.redirectEnd >= e.redirectStart, e.responseStart >= e.fetchStart].join()); });"));
    }

    /// <summary>
    /// <c>Server-Timing</c> becomes <c>PerformanceServerTiming</c> objects: the first <c>dur</c> and
    /// <c>desc</c> of each metric, a quoted value unquoted. <c>serverTiming</c> is a new frozen array of
    /// the same objects at every read, and the entry's JSON lists them.
    /// </summary>
    [Fact]
    public void ServerTimingIsParsed()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/st.js", new Reply(ContentType: Js, Body: Script,
            Headers: [("Server-Timing", "db;dur=12.5;desc=\"Database\", cache;desc=hit, total;dur=3;dur=4;desc=a;desc=b")]));

        Assert.Equal(
            "[{\"name\":\"db\",\"duration\":12.5,\"description\":\"Database\"},{\"name\":\"cache\",\"duration\":0,\"description\":\"hit\"}," +
            "{\"name\":\"total\",\"duration\":3,\"description\":\"a\"}]" +
            " | [object Array],true,false,true,[object PerformanceServerTiming],constructor,description,duration,name,toJSON" +
            " | [object PerformanceServerTiming],Failed to construct 'PerformanceServerTiming': Illegal constructor",
            Run(server,
                "load('/st.js', function () { var e = entry('/st.js'), st = e.serverTiming, ctor;" +
                "  try { new PerformanceServerTiming(); } catch (x) { ctor = x.message; }" +
                "  done(JSON.stringify(st) + ' | ' + [Object.prototype.toString.call(st), Object.isFrozen(st), st === e.serverTiming," +
                "    st[0] === e.serverTiming[0], Object.prototype.toString.call(st[0])," +
                "    Object.getOwnPropertyNames(PerformanceServerTiming.prototype).sort().join()].join() + ' | ' +" +
                "    [Object.prototype.toString.call(e.toJSON().serverTiming[0]), ctor].join()); });"));
    }

    /// <summary>
    /// A <c>fetch()</c> has an entry once the page has read its body -- through a body reader, its
    /// stream or a clone's -- and none while it has not; an answer with no body to read, a <c>HEAD</c>'s,
    /// has one at once. An XMLHttpRequest's is there as soon as it is done, and so is a beacon's.
    /// </summary>
    [Fact]
    public void AFetchHasAnEntryOnceItsBodyIsRead()
    {
        using var server = new LoopbackCookieServer();
        foreach (var path in new[] { "/read", "/unread", "/head", "/reader", "/clone", "/xhr" })
            server.Map(path, new Reply(ContentType: "application/json", Body: "{\"ok\":1}"));
        server.Map("/beacon", new Reply(204, ContentType: "text/plain"));

        Assert.Equal(
            "before read 0, after read 1 | head 1, unread 0, reader 1, clone 1 | fetch,fetch,fetch,fetch" +
            " | xmlhttprequest,200,application/json | beacon,204,0,0",
            Run(server,
                "var result = [];" +
                "fetch('/read').then(function (r) {" +
                "  result.push('before read ' + entries('/read').length);" +
                "  return r.text(); }).then(function () {" +
                "  result.push('after read ' + entries('/read').length);" +
                "  return Promise.all([fetch('/head', { method: 'HEAD' }), fetch('/unread')," +
                "    fetch('/reader').then(function (r) { return r.body.getReader().read(); })," +
                "    fetch('/clone').then(function (r) { return r.clone().text(); })]); }).then(function () {" +
                "  var x = new XMLHttpRequest(); x.open('GET', '/xhr');" +
                "  x.onload = function () {" +
                "    navigator.sendBeacon('/beacon', 'x');" +
                "    setTimeout(function () {" +
                "      var xhr = entry('/xhr'), beacon = entry('/beacon');" +
                "      done(result.join(', ') + ' | head ' + entries('/head').length + ', unread ' + entries('/unread').length +" +
                "        ', reader ' + entries('/reader').length + ', clone ' + entries('/clone').length + ' | ' +" +
                "        ['/read', '/head', '/reader', '/clone'].map(function (p) { return entry(p).initiatorType; }).join() + ' | ' +" +
                "        [xhr.initiatorType, xhr.responseStatus, xhr.contentType].join() + ' | ' +" +
                "        [beacon.initiatorType, beacon.responseStatus, beacon.encodedBodySize, beacon.decodedBodySize].join());" +
                "    }, 0); };" +
                "  x.send(); });"));
    }

    /// <summary>
    /// A frame's document is in the Resource Timing of the document the frame is in, named by its
    /// container: an <c>iframe</c> of the page's origin shows its status and type, and the frame's own
    /// timeline does not have it. One of another origin shows neither, nor its timings.
    /// </summary>
    [Fact]
    public void AFramesDocumentIsItsContainersEntry()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/frame.html", new Reply(Body: "<p>frame</p><script>top.frameEntries = performance.getEntriesByType('resource').length;</script>"));
        server.Map("/other.html", new Reply(Body: "<p>other</p>"));

        Assert.Equal(
            "iframe,non-blocking,200,text/html,http/1.1 | iframe,0,,,0 | 0",
            Run(server,
                "var own = document.getElementById('f').contentWindow, other = document.getElementById('o').contentWindow;" +
                "setTimeout(function () { var f = entry('/frame.html'), o = entry('/other.html');" +
                "  done([f.initiatorType, f.renderBlockingStatus, f.responseStatus, f.contentType, f.nextHopProtocol].join() + ' | ' +" +
                "    [o.initiatorType, o.responseStatus, o.contentType, o.nextHopProtocol, o.encodedBodySize].join() + ' | ' +" +
                "    window.frameEntries); }, 0);",
                body: $"<iframe id=\"f\" src=\"/frame.html\"></iframe><iframe id=\"o\" src=\"{server.Url("/other.html")}\"></iframe>"));
    }

    /// <summary>A worker's script is in the Resource Timing of the document that started it, as <c>other</c>.</summary>
    [Fact]
    public void AWorkersScriptIsItsDocumentsEntry()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/worker.js", new Reply(ContentType: Js, Body: "onmessage = function (e) { postMessage('up'); };"));

        Assert.Equal(
            "up,other,200,text/javascript",
            Run(server,
                "var w = new Worker('/worker.js');" +
                "w.onmessage = function (m) { setTimeout(function () { var e = entry('/worker.js');" +
                "  done([m.data, e.initiatorType, e.responseStatus, e.contentType].join()); }, 0); };" +
                "w.postMessage('hi');"));
    }

    /// <summary>
    /// A style sheet the parser finds in the head has one entry, a <c>link</c> that held up rendering,
    /// and what it imports one, as <c>css</c>; a sheet a script links once the page has loaded did not
    /// hold up rendering.
    /// </summary>
    [Fact]
    public void AStyleSheetHasOneEntry()
    {
        using var server = new LoopbackCookieServer();
        server
            .Map("/head.css", new Reply(ContentType: "text/css", Body: "@import url(\"/imported.css\"); p { color: red }"))
            .Map("/imported.css", new Reply(ContentType: "text/css", Body: "p { margin: 0 }"))
            .Map("/later.css", new Reply(ContentType: "text/css", Body: "p { padding: 0 }"));

        Assert.Equal(
            "1,link,blocking | 1,css | 1,link,non-blocking",
            Run(server,
                "window.addEventListener('load', function () {" +
                "  getComputedStyle(document.querySelector('p')).color;" +
                "  var l = document.createElement('link'); l.rel = 'stylesheet'; l.href = '/later.css';" +
                "  l.onload = l.onerror = function () { setTimeout(function () {" +
                "    getComputedStyle(document.querySelector('p')).color;" +
                "    var h = entries('/head.css'), i = entries('/imported.css'), later = entries('/later.css');" +
                "    done([h.length, h[0] && h[0].initiatorType, h[0] && h[0].renderBlockingStatus].join() + ' | ' +" +
                "      [i.length, i[0] && i[0].initiatorType].join() + ' | ' +" +
                "      [later.length, later[0] && later[0].initiatorType, later[0] && later[0].renderBlockingStatus].join()); }, 0); };" +
                "  document.head.appendChild(l); });",
                head: "<link rel=\"stylesheet\" href=\"/head.css\">",
                body: "<p>text</p>"));
    }

    /// <summary>
    /// The buffer holds 250 entries unless the page says otherwise. Once it is full a task fires
    /// <c>resourcetimingbufferfull</c> at <c>performance</c> -- its handler, then its listeners -- and
    /// what they made room for is moved in, the rest dropped; observers have every entry regardless.
    /// <c>clearResourceTimings</c> empties the buffer.
    /// </summary>
    [Fact]
    public void TheBufferHoldsWhatItsSizeAllows()
    {
        using var server = new LoopbackCookieServer();
        foreach (var name in new[] { "/1", "/2", "/3", "/4" })
            server.Map(name, new Reply(204, ContentType: "text/plain"));

        Assert.Equal(
            "true,,function,1,TypeError: Failed to execute 'setResourceTimingBufferSize' on 'Performance': 1 argument required, but only 0 present." +
            " | on Event false false true true 2, listener 2, on Event false false true true 3, listener 3" +
            " | /1 /2 /3 | /1 /2 /3 /4 | 0",
            Run(server,
                "var events = [], seen = [], err;" +
                "try { performance.setResourceTimingBufferSize(); } catch (e) { err = e.name + ': ' + e.message; }" +
                "var head = [performance instanceof EventTarget, performance.onresourcetimingbufferfull," +
                "  typeof performance.clearResourceTimings, performance.setResourceTimingBufferSize.length, err].join();" +
                "new PerformanceObserver(function (list) { list.getEntries().forEach(function (e) { seen.push(e.name.replace(location.origin, '')); }); })" +
                "  .observe({ type: 'resource' });" +
                "performance.setResourceTimingBufferSize(2);" +
                "performance.onresourcetimingbufferfull = function (ev) {" +
                "  events.push(['on', ev.constructor.name, ev.bubbles, ev.cancelable, this === performance, ev.target === performance," +
                "    performance.getEntriesByType('resource').length].join(' '));" +
                "  if (events.length === 1) performance.setResourceTimingBufferSize(3); };" +
                "performance.addEventListener('resourcetimingbufferfull', function () {" +
                "  events.push('listener ' + performance.getEntriesByType('resource').length); });" +
                "['/1', '/2', '/3', '/4'].forEach(function (p, i) { setTimeout(function () { navigator.sendBeacon(p, 'x'); }, i * 10); });" +
                "setTimeout(function () {" +
                "  var names = performance.getEntriesByType('resource').map(function (e) { return e.name.replace(location.origin, ''); }).join(' ');" +
                "  performance.clearResourceTimings();" +
                "  done(head + ' | ' + events.join(', ') + ' | ' + names + ' | ' + seen.join(' ') + ' | ' +" +
                "    performance.getEntriesByType('resource').length); }, 200);"));
    }

    /// <summary>
    /// The entry interface is Chromium's: its attributes on the prototype, in its JSON's order after the
    /// four every entry has, and not constructible. <c>resource</c> is a type an observer can ask for,
    /// and a buffered one is handed what was recorded before it. The navigation entry answers the
    /// attributes it does not carry as a document fetched without a service worker does.
    /// </summary>
    [Fact]
    public void TheEntryInterfaceIsChromiums()
    {
        using var server = new LoopbackCookieServer();
        server.Map("/a.js", new Reply(ContentType: Js, Body: Script));

        Assert.Equal(
            "connectEnd,connectStart,constructor,contentEncoding,contentType,decodedBodySize,deliveryType,domainLookupEnd," +
            "domainLookupStart,encodedBodySize,fetchStart,finalResponseHeadersStart,firstInterimResponseStart,initiatorType," +
            "nextHopProtocol,redirectEnd,redirectStart,renderBlockingStatus,requestStart,responseEnd,responseStart,responseStatus," +
            "secureConnectionStart,serverTiming,toJSON,transferSize,workerCacheLookupStart,workerFinalSourceType," +
            "workerMatchedSourceType,workerRouterEvaluationStart,workerStart" +
            " | name,entryType,startTime,duration,initiatorType,deliveryType,nextHopProtocol,renderBlockingStatus,contentType," +
            "contentEncoding,workerStart,workerRouterEvaluationStart,workerCacheLookupStart,workerMatchedSourceType," +
            "workerFinalSourceType,redirectStart,redirectEnd,fetchStart,domainLookupStart,domainLookupEnd,connectStart," +
            "secureConnectionStart,connectEnd,requestStart,responseStart,firstInterimResponseStart,finalResponseHeadersStart," +
            "responseEnd,transferSize,encodedBodySize,decodedBodySize,responseStatus,serverTiming" +
            " | Failed to construct 'PerformanceResourceTiming': Illegal constructor | true | buffered 1" +
            " | non-blocking,text/html,,0,,0,true,0,",
            Run(server,
                "load('/a.js', function () {" +
                "  var e = entry('/a.js'), ctor;" +
                "  try { new PerformanceResourceTiming(); } catch (x) { ctor = x.message; }" +
                "  new PerformanceObserver(function (list, observer) { observer.disconnect();" +
                "    var n = performance.getEntriesByType('navigation')[0];" +
                "    done([Object.getOwnPropertyNames(PerformanceResourceTiming.prototype).sort().join(), Object.keys(e.toJSON()).join(), ctor," +
                "      PerformanceObserver.supportedEntryTypes.indexOf('resource') >= 0, 'buffered ' + list.getEntries().length," +
                "      [n.renderBlockingStatus, n.contentType, n.contentEncoding, n.responseStatus, n.deliveryType, n.serverTiming.length," +
                "        n.finalResponseHeadersStart === n.responseStart, n.workerRouterEvaluationStart, n.workerMatchedSourceType].join()].join(' | ')); })" +
                "    .observe({ type: 'resource', buffered: true }); });"));
    }

    /// <summary>
    /// The scripts a host fetched for a document before running it are the document's entries once the
    /// host hands their records to the engine: a parser-inserted one in the head held up rendering, an
    /// async one did not. With the navigation's start as the time origin they are where they happened;
    /// without it, before the origin, they are clamped to it. The engine takes both for one document.
    /// </summary>
    [Theory]
    [InlineData(true, "head.js,script,blocking,200,true,true | async.js,script,non-blocking,200,true,true")]
    [InlineData(false, "head.js,script,blocking,200,false,false | async.js,script,non-blocking,200,false,false")]
    public void TheHostsFetchesAreTheDocumentsEntries(bool navigationStart, string expected)
    {
        using var server = new LoopbackCookieServer();
        server
            .Map("/head.js", new Reply(ContentType: Js, Body: Script))
            .Map("/async.js", new Reply(ContentType: Js, Body: Script));
        var url = server.LocalhostUrl("/page");
        var html = "<html><head><script src=\"/head.js\"></script><script async src=\"/async.js\"></script></head>" +
                   "<body><div id=\"out\">waiting</div><script>" + Helpers +
                   "done(['/head.js', '/async.js'].map(function (p) { var e = entry(p);" +
                   "  return [p.slice(1), e.initiatorType, e.renderBlockingStatus, e.responseStatus, e.startTime > 0, e.responseEnd > e.startTime].join(); })" +
                   ".join(' | '));</script></body></html>";

        using var profile = NewProfile();
        var fetchTiming = navigationStart ? DocumentFetchTiming.StartNavigation() : null;
        var extracted = ScriptExtractionService.ExtractAll(
            html, url, null, new ScriptFetchContext(profile, DocumentRequestContext.CreateTopLevel(new Uri(url))));
        Assert.Equal(2, extracted.ResourceTimings.Count);

        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions { Network = profile, Cookies = profile }))
        {
            DocumentFetchTiming = fetchTiming,
            DocumentResourceTimings = extracted.ResourceTimings,
        };
        using var session = engine.ExecuteInteractive(
            [.. extracted.Scripts, .. extracted.AsyncScripts], extracted.DeferredScripts, html, url);

        Assert.Equal(expected, PageProbe.OutOf(session!.SettleLoadWindow(), decode: true));
        Assert.Null(engine.DocumentFetchTiming);
        Assert.Empty(engine.DocumentResourceTimings);
    }
}
