using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;
using Broiler.Net.Http;
using Reply = Broiler.HtmlBridge.Tests.LoopbackCookieServer.Reply;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The page's session history, as Chromium keeps it: <c>pushState</c> and <c>replaceState</c> move the
/// document's URL and state, fragment navigations add entries, and <c>back</c>, <c>forward</c> and
/// <c>go</c> traverse between them in a later task -- and the host hears all of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>History was a stub</b>: <c>pushState</c> kept the state and left the URL, <c>history.length</c> was
/// one, and <c>back()</c>, <c>forward()</c> and <c>go()</c> did nothing.
/// </para>
/// <para>
/// Measured in Chromium: the URL -- <c>href</c>, <c>pathname</c>, <c>search</c>, <c>document.URL</c> --
/// moves at once; the state is a clone; a fragment-only push fires nothing; a URL of another origin is a
/// <c>SecurityError</c>; a traversal is a task after the script, its microtasks and a timer queued first,
/// firing <c>popstate</c> with the entry's state, then <c>hashchange</c> in a task of its own when the
/// fragment changed; past either end nothing happens.
/// </para>
/// </remarks>
public class SessionHistoryTests
{
    private const string PageUrl = "https://example.test/start";

    private const string Recorder =
        "var out = document.getElementById('out'), log = [];" +
        "function show() { out.textContent = log.join('|'); }" +
        "function note(entry) { log.push(entry); show(); }" +
        "function path() { return location.pathname + location.search + location.hash; }" +
        "addEventListener('popstate', function (e) { note('popstate ' + JSON.stringify(e.state) + ' ' + path() + ' ' + JSON.stringify(history.state)); });" +
        "addEventListener('hashchange', function (e) { note('hashchange ' + e.oldURL.split('/').pop() + ' ' + e.newURL.split('/').pop()); });";

    private static InteractiveSession Start(string script, string body = "")
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions()));
        var session = engine.ExecuteInteractive([Recorder + script + ";show();"], [], $"<html><body>{body}<p id=\"sec\">s</p><div id=\"out\"></div></body></html>", PageUrl);
        Assert.NotNull(session);
        return session!;
    }

    private static string Settle(string script, string body = "")
    {
        using var session = Start(script, body);
        return PageProbe.OutOf(session.SettleLoadWindow(), decode: true);
    }

    /// <summary>
    /// <c>pushState</c> moves the URL and keeps a clone of the state; <c>replaceState</c> replaces the entry;
    /// a fragment-only push fires nothing; relative URLs resolve against the URL the document has now.
    /// </summary>
    [Fact]
    public void PushStateMovesTheDocumentsUrl()
    {
        Assert.Equal(
            "/one?x=1 /one?x=1 https://example.test/one?x=1 2 {\"n\":1} false|/one?x=1#f 3 no-hashchange t=-|/two 3 {\"n\":22}|https://example.test/dir/x",
            Settle(
                "var o = { n: 1 }; history.pushState(o, '', '/one?x=1');" +
                "log.push([location.pathname + location.search, path(), document.URL, history.length, JSON.stringify(history.state), history.state === o].join(' '));" +
                "history.pushState({ n: 2 }, '', '#f');" +
                "log.push([path(), history.length, 'no-hashchange', 't=' + (document.querySelector(':target') ? document.querySelector(':target').id : '-')].join(' '));" +
                "history.replaceState({ n: 22 }, '', '/two');" +
                "log.push([path(), history.length, JSON.stringify(history.state)].join(' '));" +
                "history.pushState(null, '', '/dir/page'); var a = document.createElement('a'); a.setAttribute('href', 'x'); log.push(a.href)"));
    }

    /// <summary>A URL of another origin is a <c>SecurityError</c>, and nothing changes.</summary>
    [Fact]
    public void AnotherOriginsUrlIsRefused()
    {
        Assert.Equal(
            "SecurityError /start 1",
            Settle("try { history.pushState(null, '', 'https://other.test/x'); } catch (e) { log.push([e.name, path(), history.length].join(' ')); }"));
    }

    /// <summary>
    /// A traversal is a task after the script, its microtasks and a timer queued first: <c>popstate</c> with
    /// the entry's state -- and no <c>hashchange</c> when the path moved too; past either end, nothing.
    /// </summary>
    [Fact]
    public void ATraversalIsATaskThatFiresPopState()
    {
        Assert.Equal(
            "after back /b#h|microtask|timeout|popstate {\"n\":\"a\"} /a {\"n\":\"a\"}|forward|popstate {\"n\":\"b\"} /b#h {\"n\":\"b\"}|go -5|done",
            Settle(
                "history.replaceState({ n: 'start' }, '');" +
                "history.pushState({ n: 'a' }, '', '/a'); history.pushState({ n: 'b' }, '', '/b#h');" +
                "setTimeout(function () { note('timeout'); }, 0);" +
                "history.back(); log.push('after back ' + path());" +
                "Promise.resolve().then(function () { note('microtask'); });" +
                "setTimeout(function () { note('forward'); history.forward();" +
                "  setTimeout(function () { note('go -5'); history.go(-5); setTimeout(function () { note('done'); }, 10); }, 10); }, 10)"));
    }

    /// <summary>Fragment navigations add entries, and going back to one moves the fragment back, with its <c>hashchange</c>.</summary>
    [Fact]
    public void FragmentNavigationsAreEntries()
    {
        Assert.Equal(
            "popstate null /start#a null|popstate null /start#b null|3|hashchange start start#a|hashchange start#a start#b|popstate null /start#a null|hashchange start#b start#a",
            Settle(
                "location.hash = '#a'; location.hash = '#b'; log.push(String(history.length));" +
                "setTimeout(function () { history.back(); }, 5)"));
    }

    /// <summary><c>go(0)</c> reloads the page.</summary>
    [Fact]
    public void GoZeroReloads()
    {
        using var session = Start("history.go(0)");
        session.SettleLoadWindow();
        Assert.Equal(NavigationKind.Reload, session.TakePendingNavigation()?.Kind);
    }

    /// <summary>
    /// The host hears every push, replace and traversal; a traversal past the page's own entries is the
    /// host's to make; the host's own traversal among them fires <c>popstate</c> and is not reported back.
    /// </summary>
    [Fact]
    public void TheHostHearsThePagesHistory()
    {
        using var session = Start(
            "history.pushState({ n: 1 }, '', '/one'); location.hash = '#x'; history.replaceState({ n: 2 }, '', '/two');" +
            "document.addEventListener('keydown', function (e) { if (e.key === 'F8') history.go(-1); if (e.key === 'F9') history.go(-2); });");
        session.SettleLoadWindow();

        Assert.Equal(
            [new HistoryChange(HistoryChangeKind.Push, "https://example.test/one"), new HistoryChange(HistoryChangeKind.Push, "https://example.test/one#x"),
             new HistoryChange(HistoryChangeKind.Replace, "https://example.test/two")],
            session.TakeHistoryChanges());

        session.SetSessionHistory(before: 1, after: 0);
        session.DispatchKey(new KeyboardInput(KeyboardInputKind.Down, "F8", "F8") { KeyCode = 119 });
        session.SettleLoadWindow();
        Assert.Equal([new HistoryChange(HistoryChangeKind.Traverse, "https://example.test/one", -1)], session.TakeHistoryChanges());

        session.DispatchKey(new KeyboardInput(KeyboardInputKind.Down, "F9", "F9") { KeyCode = 120 });
        session.SettleLoadWindow();
        Assert.Equal([new HistoryChange(HistoryChangeKind.TraverseAway, "https://example.test/one", -2)], session.TakeHistoryChanges());

        Assert.True(session.TraverseHistory(1));
        Assert.False(session.TraverseHistory(5));
        Assert.Empty(session.TakeHistoryChanges());
        Assert.EndsWith("popstate {\"n\":1} /one {\"n\":1}|popstate {\"n\":2} /two {\"n\":2}", PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }

    /// <summary>
    /// A frame's history is its own -- its state, and its URL, which a <c>srcdoc</c> frame cannot move to the
    /// page's -- and its length the page's, which counts its entries too.
    /// </summary>
    [Fact]
    public void AFramesHistoryIsItsOwn()
    {
        const string frameScript =
            "try { history.pushState(null, '', '#framed'); } catch (e) { parent.note('frame ' + e.name); }" +
            "history.pushState({ f: 1 }, ''); parent.note('frame ' + location.href + ' ' + history.length + ' ' + JSON.stringify(history.state));";

        Assert.Equal(
            "frame SecurityError|frame about:srcdoc 2 {\"f\":1}|page /start 2 null",
            Settle(
                "setTimeout(function () { note('page ' + path() + ' ' + history.length + ' ' + JSON.stringify(history.state)); }, 50);",
                $"<iframe srcdoc=\"&lt;script&gt;{frameScript.Replace("'", "&#39;").Replace("\"", "&quot;")}&lt;/script&gt;\"></iframe>"));
    }

    /// <summary>
    /// A traversal puts the scroll back where its entry was left -- after <c>popstate</c>, which still sees the
    /// scroll the traversal left -- unless the entry's <c>scrollRestoration</c> is <c>manual</c>, which leaves the scroll where it is;
    /// the mode is each entry's own, and a value that is neither is ignored. Chromium's figures, step for step.
    /// </summary>
    [Fact]
    public void ATraversalRestoresTheScroll()
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = () => new DeclaredBoxLayoutView(new Dictionary<string, System.Drawing.RectangleF>
            {
                ["root"] = new(0, 0, 1024, 5000),
            }),
        }));
        using var session = engine.ExecuteInteractive(
            ["var out = document.getElementById('out'), log = [];" +
             "function note(entry) { log.push(entry); out.textContent = log.join('|'); }" +
             "function later(f) { setTimeout(f, 50); }" +
             "addEventListener('popstate', function (e) { note('popstate ' + JSON.stringify(e.state) + ' y=' + scrollY); });" +
             "history.replaceState({ n: 0 }, '', '?r=0'); scrollTo(0, 300);" +
             "history.pushState({ n: 1 }, '', '?r=1'); scrollTo(0, 1200);" +
             "history.pushState({ n: 2 }, '', '?r=2'); scrollTo(0, 2000);" +
             "history.back(); later(function () { note('after back y=' + scrollY); history.back();" +
             "  later(function () { note('after back 2 y=' + scrollY); history.forward();" +
             "    later(function () { note('after forward y=' + scrollY + ' ' + history.scrollRestoration);" +
             "      history.scrollRestoration = 'manual'; history.scrollRestoration = 'bogus'; note(history.scrollRestoration);" +
             "      scrollTo(0, 50); history.back(); later(function () { note('manual back y=' + scrollY + ' ' + history.scrollRestoration);" +
             "        scrollTo(0, 700); history.forward(); later(function () { note('forward to manual y=' + scrollY + ' ' + history.scrollRestoration); }); }); }); }); });"],
            [], "<html id=\"root\"><body><div id=\"out\"></div></body></html>", PageUrl)!;

        Assert.Equal(
            "popstate {\"n\":1} y=2000|after back y=1200|popstate {\"n\":0} y=1200|after back 2 y=300|" +
            "popstate {\"n\":1} y=300|after forward y=1200 auto|manual|popstate {\"n\":0} y=50|manual back y=300 auto|" +
            "popstate {\"n\":1} y=700|forward to manual y=700 manual",
            PageProbe.OutOf(session!.SettleLoadWindow(), decode: true));
    }

    /// <summary>
    /// One history for the page and its frame: the page's <c>back()</c> undoes its own push, the next the
    /// frame's, and <c>forward()</c> redoes the frame's, each heard by the document it moves; the length counts
    /// both (measured in Chromium).
    /// </summary>
    [Fact]
    public void ThePageGoesBackThroughItsFramesEntries()
    {
        var log = Settle(
            "var fw = document.getElementById('f').contentWindow;" +
            "fw.addEventListener('popstate', function (e) { note('frame popstate ' + JSON.stringify(e.state)); });" +
            "fw.history.pushState({ f: 1 }, ''); note('frame pushed ' + history.length);" +
            "history.pushState({ p: 1 }, '', '?joint=1'); note('page pushed ' + history.length);" +
            "history.back(); setTimeout(function () { history.back(); setTimeout(function () { history.forward();" +
            "  setTimeout(function () { note('frame state ' + JSON.stringify(fw.history.state) + ' page ' + path()); }, 50); }, 50); }, 50);",
            "<iframe id=\"f\" srcdoc=\"&lt;p&gt;frame&lt;/p&gt;\"></iframe>");

        Assert.Equal(
            "frame pushed 2|page pushed 3|popstate null /start null|frame popstate null|frame popstate {\"f\":1}|frame state {\"f\":1} page /start",
            log);
    }

    /// <summary>A frame's entry is the window's too: the host hears it at the page's URL, and its back reaches it.</summary>
    [Fact]
    public void TheHostHearsTheFramesEntries()
    {
        using var session = Start(
            "var fw = document.getElementById('f').contentWindow;" +
            "fw.addEventListener('popstate', function (e) { note('frame popstate ' + JSON.stringify(e.state)); });" +
            "fw.history.pushState({ f: 1 }, '');",
            "<iframe id=\"f\" srcdoc=\"&lt;p&gt;frame&lt;/p&gt;\"></iframe>");
        session.SettleLoadWindow();

        Assert.Equal([new HistoryChange(HistoryChangeKind.Push, PageUrl)], session.TakeHistoryChanges());
        Assert.True(session.TraverseHistory(-1));
        Assert.Equal("frame popstate null", PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
        Assert.Empty(session.TakeHistoryChanges());
    }

    /// <summary>
    /// A frame's navigation to another document is an entry of the joint history, and going back to it loads
    /// the document the frame left again.
    /// </summary>
    [Fact]
    public void AFramesNavigationIsAnEntryToo()
    {
        using var server = new LoopbackCookieServer()
            .Map("/a", new Reply(Body: "<html><body><p id=\"which\">a</p></body></html>"))
            .Map("/b", new Reply(Body: "<html><body><p id=\"which\">b</p></body></html>"));
        using var profile = new BrowserNetworkSession(new BrowserNetworkSessionOptions { Timeout = TimeSpan.FromSeconds(20) });
        using var session = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions { Network = profile, Cookies = profile }))
            .ExecuteInteractive(
                ["var out = document.getElementById('out'), log = [];" +
                 "function note(entry) { log.push(entry); out.textContent = log.join('|'); }" +
                 "var f = document.getElementById('f'); f.contentWindow;" +
                 "function which() { var p = f.contentDocument && f.contentDocument.getElementById('which'); return p ? p.textContent : 'none'; }" +
                 "note('start ' + which() + ' ' + history.length);" +
                 "f.contentWindow.location.href = '/b';" +
                 "setTimeout(function () { note('navigated ' + which() + ' ' + history.length); history.back();" +
                 "  setTimeout(function () { note('back ' + which() + ' ' + history.length); }, 200); }, 200);"],
                [], $"<!DOCTYPE html><html><body><div id=\"out\"></div><iframe id=\"f\" src=\"{server.LocalhostUrl("/a")}\"></iframe></body></html>",
                server.LocalhostUrl("/page"))!;

        Assert.Equal("start a 1|navigated b 2|back a 2", PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
        Assert.Equal(2, server.RequestsFor("/a").Length);
    }
}
