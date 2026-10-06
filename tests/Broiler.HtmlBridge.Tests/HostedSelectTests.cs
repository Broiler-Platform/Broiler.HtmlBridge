using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A user's choice in a select the host draws its own control for reaches the page's select, as Chromium
/// fires a user's change.
/// </summary>
/// <remarks>
/// <b>The choice stayed the host's</b>: the page's select kept its value and heard nothing. Measured in
/// Chromium: <c>input</c> then <c>change</c>, trusted plain events that bubble
/// and are not cancelable, the value already new, <c>:user-valid</c> from the <c>change</c> on.
/// </remarks>
public class HostedSelectTests
{
    private const string PageUrl = "https://example.test/select";

    /// <summary>The page's select takes the user's choice, with its <c>input</c> and <c>change</c>; the same choice again, or a disabled select, changes nothing.</summary>
    [Fact]
    public void TheUsersChoiceIsThePagesSelects()
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions()));
        using var session = engine.ExecuteInteractive(
            ["var out = document.getElementById('out'), log = [], s = document.getElementById('s');" +
             "function note(entry) { log.push(entry); out.textContent = log.join('|'); }" +
             "['input', 'change'].forEach(function (t) { s.addEventListener(t, function (e) {" +
             "  note(t + ' ' + e.isTrusted + ' ' + e.bubbles + ' ' + e.cancelable + ' ' + e.constructor.name + ' ' + s.value + ' ' + s.matches(':user-valid')); }); });" +
             "document.getElementById('f').addEventListener('change', function () { note('form heard change'); });"],
            [], "<html><body><form id=\"f\"><select id=\"x\" disabled><option>p</option><option>q</option></select>" +
                "<select id=\"s\" name=\"s\" required><option value=\"\">none</option><option value=\"a\">A</option><option value=\"b\">B</option></select></form>" +
                "<div id=\"out\"></div></body></html>", PageUrl);
        Assert.NotNull(session);
        session!.SettleLoadWindow();

        Assert.False(session.SelectOptionByUser(0, 1));
        Assert.True(session.SelectOptionByUser(1, 1));
        Assert.False(session.SelectOptionByUser(1, 1));

        Assert.Equal(
            "input true true false Event a false|change true true false Event a true|form heard change",
            PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }
}
