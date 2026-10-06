using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A fragment navigation's events come as Chromium fires them: <c>popstate</c> inside the navigation,
/// <c>hashchange</c> in a later task.
/// </summary>
/// <remarks>
/// <c>hashchange</c> fired inside the <c>location.hash = x</c> that caused it, and no <c>popstate</c> fired
/// at all. Measured in Chromium: <c>popstate</c> (state <c>null</c>, not bubbling, not cancelable) at once,
/// with <c>history.state</c> <c>null</c> after it; <c>hashchange</c> after the script's microtasks, taking
/// its turn with the timers, one per navigation; neither for the fragment already in hand; a frame's at the
/// frame's window only.
/// </remarks>
public class FragmentNavigationEventTests
{
    private const string PageUrl = "https://example.test/page";

    private const string Recorder =
        "var out = document.getElementById('out'), log = [];" +
        "function show() { out.textContent = log.join('|'); }" +
        "function note(entry) { log.push(entry); show(); }" +
        "function tail(url) { return url.split('/').pop(); }";

    private static InteractiveSession Start(string script, string url = PageUrl, string body = "")
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions()));
        var session = engine.ExecuteInteractive([Recorder + script + ";show();"], [], $"<html><body>{body}<p id=\"sec\">s</p><div id=\"out\"></div></body></html>", url);
        Assert.NotNull(session);
        return session!;
    }

    private static string Settle(string script, string url = PageUrl, string body = "")
    {
        using var session = Start(script, url, body);
        return PageProbe.OutOf(session.SettleLoadWindow(), decode: true);
    }

    /// <summary>
    /// <c>popstate</c> fires inside each navigation and clears <c>history.state</c>; each <c>hashchange</c>
    /// comes after the script and its microtasks, in turn with the timers set around it.
    /// </summary>
    [Fact]
    public void PopStateFiresAtOnceAndHashChangeLater()
    {
        Assert.Equal(
            "popstate null true false false #a1|after #a1, state null|popstate null true false false #a2|end|microtask|" +
            "timeout before|hashchange page page#a1 true false #a2|timeout after|hashchange page#a1 page#a2 true false #a2",
            Settle(
                "addEventListener('popstate', function (e) { log.push(['popstate', JSON.stringify(e.state), e.isTrusted, e.bubbles, e.cancelable, location.hash].join(' ')); show(); });" +
                "addEventListener('hashchange', function (e) { log.push(['hashchange', tail(e.oldURL), tail(e.newURL), e.isTrusted, e.cancelable, location.hash].join(' ')); show(); });" +
                "history.replaceState({ s: 1 }, '');" +
                "setTimeout(function () { log.push('timeout before'); show(); }, 0);" +
                "location.hash = '#a1'; log.push('after ' + location.hash + ', state ' + JSON.stringify(history.state));" +
                "Promise.resolve().then(function () { log.push('microtask'); show(); });" +
                "setTimeout(function () { log.push('timeout after'); show(); }, 0);" +
                "location.hash = '#a2'; log.push('end')"));
    }

    /// <summary>The page's <c>onpopstate</c> and <c>onhashchange</c> run too, each after the listeners.</summary>
    [Fact]
    public void ThePagesHandlersRun()
    {
        Assert.Equal(
            "listener popstate|onpopstate|after|listener hashchange|onhashchange",
            Settle(
                "addEventListener('popstate', function () { log.push('listener popstate'); });" +
                "addEventListener('hashchange', function () { log.push('listener hashchange'); show(); });" +
                "onpopstate = function () { log.push('onpopstate'); };" +
                "onhashchange = function () { log.push('onhashchange'); show(); };" +
                "location.assign('#x'); log.push('after')"));
    }

    /// <summary>The fragment already in hand fires neither, and neither does <c>pushState</c>.</summary>
    [Fact]
    public void TheFragmentInHandFiresNeither()
    {
        Assert.Equal(
            "end",
            Settle(
                "['popstate', 'hashchange'].forEach(function (t) { addEventListener(t, function () { log.push(t); show(); }); });" +
                "location.hash = '#first'; location.replace('#first'); history.pushState(null, '', '#pushed'); log.push('end')",
                PageUrl + "#first"));
    }

    /// <summary>A frame's fragment navigation fires both at the frame's window, and nothing at the page's.</summary>
    [Fact]
    public void AFramesFragmentNavigationFiresAtTheFrame()
    {
        const string frameScript =
            "addEventListener('popstate', function (e) { parent.note('frame popstate ' + JSON.stringify(e.state)); });" +
            "addEventListener('hashchange', function (e) { parent.note('frame hashchange ' + parent.tail(e.newURL)); });" +
            "location.hash = 'x'; parent.note('frame after ' + location.hash);";

        Assert.Equal(
            "frame popstate null|frame after #x|frame hashchange about:srcdoc#x",
            Settle(
                "['popstate', 'hashchange'].forEach(function (t) { addEventListener(t, function () { log.push('page ' + t); show(); }); });",
                body: $"<iframe id=\"fr\" srcdoc=\"&lt;script&gt;{frameScript.Replace("'", "&#39;")}&lt;/script&gt;\"></iframe>"));
    }

    /// <summary>
    /// The host's link into the page fires <c>popstate</c> inside the call, and <c>hashchange</c> when the
    /// host next runs the page's tasks.
    /// </summary>
    [Fact]
    public void TheHostsFragmentNavigationFiresHashChangeAsATask()
    {
        using var session = Start(
            "['popstate', 'hashchange'].forEach(function (t) { addEventListener(t, function () { log.push(t + ' ' + location.hash); show(); }); });");
        session.SettleLoadWindow();

        Assert.True(session.NavigateToFragment(PageUrl + "#sec"));
        Assert.Equal("popstate #sec", PageProbe.OutOf(session.CurrentHtml(), decode: true));
        Assert.Equal("popstate #sec|hashchange #sec", PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }
}
