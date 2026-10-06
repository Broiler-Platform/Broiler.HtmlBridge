using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;

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
}
