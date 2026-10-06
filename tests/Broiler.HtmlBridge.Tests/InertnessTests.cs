using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Inert elements as Chromium has them -- an <c>inert</c> attribute's, and everything behind a modal dialog
/// -- which are neither focused nor hit; and a dialog's focus, which goes into it as it opens, round it with
/// Tab, and back as it closes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only a press knew.</b> A modal dialog kept the pointer from what was behind it, but <c>focus()</c>,
/// Tab and a press's focus all reached the page behind; the <c>inert</c> attribute did nothing; and a dialog
/// that opened left focus where it was.
/// </para>
/// <para>Measured in Chromium.</para>
/// </remarks>
public class InertnessTests
{
    private const string PageUrl = "https://example.test/inert";

    private const string Recorder =
        "var out = document.getElementById('out'), log = [];" +
        "function show() { out.textContent = log.join('|'); }" +
        "function note(entry) { log.push(entry); show(); }" +
        "function ae() { var a = document.activeElement; return a ? (a.id || a.tagName) : 'none'; }";

    private static InteractiveSession Start(string script, string body, IReadOnlyDictionary<string, System.Drawing.RectangleF>? boxes = null)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = boxes is null ? null : () => new DeclaredBoxLayoutView(boxes),
        }));
        var session = engine.ExecuteInteractive(
            [Recorder + script + ";show();"], [], $"<html id=\"root\"><body id=\"body\">{body}<div id=\"out\"></div></body></html>", PageUrl);
        Assert.NotNull(session);
        return session!;
    }

    private static string Settle(string script, string body)
    {
        using var session = Start(script, body);
        return PageProbe.OutOf(session.SettleLoadWindow(), decode: true);
    }

    /// <summary>
    /// <c>showModal()</c> focuses the first focusable element in the dialog; <c>focus()</c> behind it does
    /// nothing while one in it works; <c>close()</c> gives focus back to what had it.
    /// </summary>
    [Fact]
    public void FocusDoesNotReachBehindAModalDialog()
    {
        var log = Settle(
            "document.getElementById('field').focus(); var d = document.getElementById('d');" +
            "d.showModal(); note('modal ' + ae());" +
            "document.getElementById('before').focus(); note('behind ' + ae());" +
            "document.getElementById('field').focus(); note('field ' + ae());" +
            "document.getElementById('d3').focus(); note('inside ' + ae());" +
            "d.close(); note('closed ' + ae());",
            "<button id=\"before\">before</button><input id=\"field\"><dialog id=\"d\"><p>text</p><button id=\"d1\">one</button><input id=\"d2\"><button id=\"d3\">three</button></dialog>");

        Assert.Equal("modal d1|behind d1|field d1|inside d3|closed field", log);
    }

    /// <summary>
    /// The dialog focusing steps, as Chromium takes them: an <c>autofocus</c> element first, else the first
    /// focusable one, else the dialog itself -- whose own <c>autofocus</c> changes nothing; <c>show()</c> too.
    /// </summary>
    [Fact]
    public void ADialogFocusesWhatChromiumFocuses()
    {
        var log = Settle(
            "function open(id, modal) { var x = document.getElementById(id); if (modal) x.showModal(); else x.show(); note(id + ' ' + ae()); x.close(); }" +
            "open('e', true); open('f', true); open('g', true); open('d', false);" +
            "var e = document.getElementById('e'); e.show(); e.focus(); note('focus() ' + ae()); e.close();",
            "<dialog id=\"d\"><button id=\"d1\">one</button></dialog><dialog id=\"e\"><p>nothing to focus</p></dialog>" +
            "<dialog id=\"f\"><button id=\"f1\">f1</button><input id=\"f2\" autofocus></dialog><dialog id=\"g\" autofocus><button id=\"g1\">g1</button></dialog>");

        Assert.Equal("e e|f f2|g g1|d d1|focus() e", log);
    }

    /// <summary>Tab in a modal dialog goes round its own elements, and Shift+Tab back (measured: no stop outside it).</summary>
    [Fact]
    public void TabGoesRoundAModalDialog()
    {
        using var session = Start(
            "document.getElementById('d').showModal(); note(ae());" +
            "document.addEventListener('focusin', function (e) { note(e.target.id); });",
            "<button id=\"before\">before</button><dialog id=\"d\"><button id=\"d1\">one</button><input id=\"d2\"><button id=\"d3\">three</button></dialog><button id=\"after\">after</button>");
        session.SettleLoadWindow();

        for (var i = 0; i < 4; i++)
            Key(session, "Tab", shift: false);
        Key(session, "Tab", shift: true);
        Key(session, "Tab", shift: true);

        Assert.Equal("d1|d2|d3|d1|d2|d1|d3", PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }

    /// <summary>
    /// An <c>inert</c> element is neither focused nor hit: <c>focus()</c> does nothing, a Tab passes it, and a
    /// press on it is a press on what it is in -- which, not being focusable, takes focus from the field.
    /// </summary>
    [Fact]
    public void AnInertElementIsNeitherFocusedNorHit()
    {
        using var session = Start(
            "var ib = document.getElementById('ib'); note(document.getElementById('iner').inert + ' ' + ib.inert);" +
            "ib.focus(); note('focus ' + ae()); document.getElementById('fld').focus();" +
            "['pointerdown', 'click', 'focusout'].forEach(function (t) { document.addEventListener(t, function (e) { note(t + ' ' + (e.target.id || e.target.tagName)); }); });",
            "<div id=\"wrap\"><div id=\"iner\" inert><button id=\"ib\">inert</button></div><input id=\"fld\"></div>",
            new Dictionary<string, System.Drawing.RectangleF>
            {
                ["root"] = new(0, 0, 1024, 768),
                ["body"] = new(0, 0, 1024, 300),
                ["wrap"] = new(20, 20, 300, 200),
                ["iner"] = new(30, 30, 200, 60),
                ["ib"] = new(30, 30, 100, 30),
                ["fld"] = new(30, 140, 150, 24),
            });
        session.SettleLoadWindow();

        session.DispatchPointer(new PointerInput(PointerInputKind.Down, 50, 40) { Buttons = 1 });
        session.DispatchPointer(new PointerInput(PointerInputKind.Up, 50, 40));
        Key(session, "Tab", shift: false);

        Assert.Equal("true false|focus body|pointerdown wrap|focusout fld|click wrap", PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
        Assert.Equal("fld", Probe(session, "ae()"));
    }

    /// <summary>A press on an open dialog's content that cannot be focused focuses the dialog (measured).</summary>
    [Fact]
    public void APressOnAnOpenDialogFocusesIt()
    {
        using var session = Start(
            "document.getElementById('d').show(); document.getElementById('field').focus();",
            "<input id=\"field\"><dialog id=\"d\"><p id=\"pp\">text</p><button id=\"d1\">one</button></dialog>",
            new Dictionary<string, System.Drawing.RectangleF>
            {
                ["root"] = new(0, 0, 1024, 768),
                ["body"] = new(0, 0, 1024, 300),
                ["field"] = new(10, 500, 150, 24),
                ["d"] = new(20, 20, 300, 150),
                ["pp"] = new(40, 40, 260, 18),
                ["d1"] = new(40, 70, 60, 24),
            });
        session.SettleLoadWindow();

        session.DispatchPointer(new PointerInput(PointerInputKind.Down, 100, 45) { Buttons = 1 });
        session.DispatchPointer(new PointerInput(PointerInputKind.Up, 100, 45));

        Assert.Equal("d", Probe(session, "ae()"));
    }

    /// <summary>
    /// A modal dialog taken out of its document is still open but modal no more: the page behind it takes focus
    /// again, it is not <c>:modal</c> once it is back, <c>showModal()</c> throws as for any non-modal dialog, and
    /// one moved within the document is the same (measured). It stayed modal, and
    /// blocked the page again once it was put back.
    /// </summary>
    [Fact]
    public void AModalDialogTakenOutOfItsDocumentIsModalNoMore()
    {
        var log = Settle(
            "var d = document.getElementById('d'), holder = document.getElementById('holder');" +
            "document.getElementById('before').focus(); d.showModal(); note('modal ' + d.matches(':modal') + ' ' + ae());" +
            "d.remove(); note('removed open=' + d.open + ' modal=' + d.matches(':modal') + ' ' + ae());" +
            "document.getElementById('after').focus(); note('behind ' + ae());" +
            "holder.appendChild(d); note('back modal=' + d.matches(':modal'));" +
            "try { d.showModal(); note('showModal ok'); } catch (e) { note(e.name + ': ' + e.message); }" +
            "d.close(); note('closed ' + d.open + ' ' + ae());" +
            "d.showModal(); document.body.appendChild(d); note('moved modal=' + d.matches(':modal') + ' open=' + d.open);",
            "<button id=\"before\">before</button><div id=\"holder\"><dialog id=\"d\"><button id=\"inside\">inside</button></dialog></div>" +
            "<button id=\"after\">after</button>");

        Assert.Equal(
            "modal true inside|removed open=true modal=false body|behind after|back modal=false|" +
            "InvalidStateError: Failed to execute 'showModal' on 'HTMLDialogElement': The dialog is already open as a non-modal dialog, " +
            "and therefore cannot be opened as a modal dialog.|closed false after|moved modal=false open=true", log);
    }

    /// <summary>
    /// A focused field made inert keeps focus through the task, its microtasks and a timer set then, and loses it
    /// at the next frame's style update, in a task: a trusted <c>blur</c> and <c>focusout</c> with no
    /// <c>relatedTarget</c>, the body already active (measured). It kept focus.
    /// </summary>
    [Fact]
    public void AFocusedElementMadeInertLosesFocus()
    {
        var log = Settle(
            "var i = document.getElementById('i'), wrap = document.getElementById('wrap');" +
            "['blur', 'focusout'].forEach(function (t) { i.addEventListener(t, function (e) {" +
            "  note(t + ' ae=' + ae() + ' trusted=' + e.isTrusted + ' related=' + e.relatedTarget); }); });" +
            "i.focus(); wrap.inert = true; note('sync ' + ae());" +
            "Promise.resolve().then(function () { note('micro ' + ae()); });" +
            "setTimeout(function () { note('t0 ' + ae()); }, 0);" +
            "setTimeout(function () { note('later ' + ae()); }, 50);",
            "<div id=\"wrap\"><input id=\"i\"></div>");

        Assert.Equal(
            "sync i|micro i|t0 i|blur ae=body trusted=true related=null|focusout ae=body trusted=true related=null|later body", log);
    }

    /// <summary>
    /// What else takes focus away the same way -- a field disabled, not displayed, or hidden -- and what does not:
    /// a read-only field keeps it, and so does one made inert and then not again before the frame (measured).
    /// </summary>
    [Theory]
    [InlineData("i.disabled = true", true)]
    [InlineData("i.style.display = 'none'", true)]
    [InlineData("wrap.hidden = true", true)]
    [InlineData("i.style.visibility = 'hidden'", true)]
    [InlineData("i.readOnly = true", false)]
    [InlineData("wrap.inert = true; wrap.inert = false", false)]
    public void WhatTakesFocusAway(string change, bool loses)
    {
        var log = Settle(
            "var i = document.getElementById('i'), wrap = document.getElementById('wrap');" +
            "i.addEventListener('blur', function () { note('blur'); });" +
            "i.focus(); " + change + ";" +
            "setTimeout(function () { note('later ' + ae()); }, 50);",
            "<div id=\"wrap\"><input id=\"i\"></div>");

        Assert.Equal(loses ? "blur|later body" : "later i", log);
    }

    private static string Probe(InteractiveSession session, string expression)
    {
        session.RunJavaScriptUrl("javascript:void (log = [], note(" + expression + "))");
        return PageProbe.OutOf(session.SettleLoadWindow(), decode: true);
    }

    private static void Key(InteractiveSession session, string key, bool shift)
    {
        session.DispatchKey(new KeyboardInput(KeyboardInputKind.Down, key, key) { KeyCode = 9, ShiftKey = shift });
        session.DispatchKey(new KeyboardInput(KeyboardInputKind.Up, key, key) { KeyCode = 9, ShiftKey = shift });
    }
}
