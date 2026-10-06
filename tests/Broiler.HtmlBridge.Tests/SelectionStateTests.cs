using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A select's options as Chromium selects them: each selected or not, a multiple select holding any number
/// and submitting each, a select-one one -- through <c>option.selected</c>, <c>selectedIndex</c>,
/// <c>value</c>, <c>selectedOptions</c>, a reset, <c>multiple</c>, and the user's choice in the host's list.
/// </summary>
/// <remarks>
/// <para>
/// <b>A select had one selected index.</b> A multiple select held and submitted one option;
/// <c>option.selected</c> did not exist, and <c>select.options</c> threw. A select-one whose markup marked two
/// options chose the first, where Chromium chooses the last.
/// </para>
/// <para>Measured in Chromium.</para>
/// </remarks>
public class SelectionStateTests
{
    private const string PageUrl = "https://example.test/select";

    private const string Form =
        "<form id=\"f\"><select id=\"m\" name=\"m\" multiple><option value=\"a\" selected>A</option><option value=\"b\">B</option>" +
        "<option value=\"c\" selected>C</option><optgroup label=\"g\"><option value=\"d\">D</option></optgroup></select>" +
        "<select id=\"s\" name=\"s\"><option value=\"x\">X</option><option value=\"y\" selected>Y</option><option value=\"z\" selected>Z</option></select></form>";

    private const string Recorder =
        "var out = document.getElementById('out'), log = [], m = document.getElementById('m'), s = document.getElementById('s');" +
        "function show() { out.textContent = log.join('|'); }" +
        "function note(entry) { log.push(entry); show(); }" +
        "function st(el) { return [el.selectedIndex, JSON.stringify(el.value), Array.from(el.selectedOptions).map(function (o) { return o.value; }).join(','), Array.from(el.options).map(function (o) { return o.selected ? 1 : 0; }).join('')].join(' '); }" +
        "function fd() { return Array.from(new FormData(document.getElementById('f'))).map(function (e) { return e[0] + '=' + e[1]; }).join('&'); }";

    private static InteractiveSession Start(string script, string body = Form)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions()));
        var session = engine.ExecuteInteractive(
            [Recorder + script + ";show();"], [], $"<html><body>{body}<div id=\"out\"></div></body></html>", PageUrl);
        Assert.NotNull(session);
        return session!;
    }

    private static string Settle(string script, string body = Form)
    {
        using var session = Start(script, body);
        return PageProbe.OutOf(session.SettleLoadWindow(), decode: true);
    }

    /// <summary>
    /// As the markup says: the multiple select's two marked options, the select-one's last marked one; each
    /// submitted; the collections' members and identity.
    /// </summary>
    [Fact]
    public void ASelectIsAsItsMarkupSays()
    {
        var log = Settle(
            "note(st(m) + ' ' + m.type + ' ' + m.multiple + ' ' + m.length + ' ' + m.options.length);" +
            "note(st(s) + ' ' + s.type + ' ' + s.multiple);" +
            "note(fd());" +
            "note((m.options === m.options) + ' ' + (m.selectedOptions === m.selectedOptions) + ' ' + Object.prototype.toString.call(m.selectedOptions) + ' ' +" +
            "  m.options[3].index + ' ' + m.item(1).value + ' ' + m.options.item(2).value + ' ' + m.options.selectedIndex);");

        Assert.Equal(
            "0 \"a\" a,c 1010 select-multiple true 4 4|2 \"z\" z 001 select-one false|m=a&m=c&s=z|true true [object HTMLCollection] 3 b c 0", log);
    }

    /// <summary>
    /// <c>option.selected</c>: a multiple select adds and takes away one option; a select-one keeps the one set
    /// alone, and with its only one taken away selects its first again (measured).
    /// </summary>
    [Fact]
    public void OptionSelectedChangesOneOption()
    {
        var log = Settle(
            "m.options[1].selected = true; note(st(m)); m.options[0].selected = false; note(st(m));" +
            "s.options[0].selected = true; note(st(s)); s.options[0].selected = false; note(st(s)); note(fd());");

        Assert.Equal("0 \"a\" a,b,c 1110|1 \"b\" b,c 0110|0 \"x\" x 100|0 \"x\" x 100|m=b&m=c&s=x", log);
    }

    /// <summary>
    /// <c>selectedIndex</c> and <c>value</c> select one option alone, or none for one that is not there; a
    /// <c>selected</c> attribute then added to an option a script deselected does not select it (measured).
    /// </summary>
    [Fact]
    public void SelectedIndexAndValueSelectOneOption()
    {
        var log = Settle(
            "m.selectedIndex = 3; note(st(m)); m.value = 'c'; note(st(m)); m.value = 'nope'; note(st(m));" +
            "s.value = 'nope'; note(st(s)); s.selectedIndex = -1; note(st(s));" +
            "m.selectedIndex = -1; m.options[2].defaultSelected = true; note(st(m)); note(fd());");

        Assert.Equal(
            "3 \"d\" d 0001|2 \"c\" c 0010|-1 \"\"  0000|-1 \"\"  000|-1 \"\"  000|-1 \"\"  0000|", log);
    }

    /// <summary>
    /// A reset puts both selects back as their markup says; taking away <c>multiple</c> keeps the first selected
    /// option, as Chromium does; adding a selected option to a select-one selects it alone (measured).
    /// </summary>
    [Fact]
    public void ResetMultipleAndAddAreChromiums()
    {
        var log = Settle(
            "m.selectedIndex = 1; s.selectedIndex = 0; document.getElementById('f').reset(); note(st(m) + ' / ' + st(s));" +
            "m.multiple = false; note(st(m) + ' ' + m.type); m.multiple = true; note(st(m) + ' ' + m.type);" +
            "var o = document.createElement('option'); o.value = 'n'; o.selected = true; m.add(o); note(st(m));" +
            "var o2 = document.createElement('option'); o2.value = 'n2'; o2.selected = true; s.add(o2); note(st(s));");

        Assert.Equal(
            "0 \"a\" a,c 1010 / 2 \"z\" z 001|0 \"a\" a 1000 select-one|0 \"a\" a 1000 select-multiple|0 \"a\" a,n 10001|3 \"n2\" n2 0001", log);
    }

    /// <summary>
    /// The user's choice in the host's list for a multiple select: the page's select takes the whole choice,
    /// with one <c>input</c> and one <c>change</c>; the same choice again fires nothing (measured: one pair per change).
    /// </summary>
    [Fact]
    public void TheUsersChoicesAreThePagesSelects()
    {
        using var session = Start(
            "['input', 'change'].forEach(function (t) { m.addEventListener(t, function (e) { note(t + ' ' + e.isTrusted + ' ' + e.bubbles + ' ' + st(m)); }); });");
        session.SettleLoadWindow();

        Assert.True(session.SelectOptionsByUser(0, [1, 3]));
        Assert.False(session.SelectOptionsByUser(0, [3, 1]));
        Assert.True(session.SelectOptionByUser(1, 0));

        session.RunJavaScriptUrl("javascript:void note(fd())");
        Assert.Equal(
            "input true true 1 \"b\" b,d 0101|change true true 1 \"b\" b,d 0101|m=b&m=d&s=x",
            PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }

    /// <summary>The window's projection of the page carries each selected option of a multiple select, as the markup the host reads.</summary>
    [Fact]
    public void TheProjectionCarriesEverySelectedOption()
    {
        using var session = Start("m.options[1].selected = true; m.options[0].selected = false;");
        var html = session.SettleLoadWindow();

        Assert.Contains("<option value=\"b\" selected=\"\">B</option>", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"c\" selected=\"\">C</option>", html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"a\">A</option>", html, StringComparison.Ordinal);
    }
}
