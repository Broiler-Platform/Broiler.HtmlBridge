using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A <c>&lt;dialog&gt;</c> as Chromium has it: displayed while it is open, and opened and closed with its
/// events -- <c>beforetoggle</c> at once, <c>toggle</c> as a task, <c>close</c> with the next frame, and
/// <c>cancel</c> for a close request.
/// </summary>
/// <remarks>
/// <para>
/// <b>A dialog opened by its <c>open</c> attribute was <c>display: none</c></b> to the page's scripts and to
/// the bridge's hit test, though the window painted it, so a press on it reached the page as a press on
/// <c>&lt;html&gt;</c>. And no dialog fired anything: no <c>close</c>, no <c>toggle</c>, no <c>cancel</c>;
/// there was no <c>requestClose()</c>, and Escape left a modal dialog open.
/// </para>
/// <para>
/// Measured in Chromium. Its <c>close</c> comes with the animation frame, so
/// a page that draws no frames never hears it; here it is the bridge's next frame.
/// </para>
/// </remarks>
public class DialogEventTests
{
    private const string PageUrl = "https://example.test/dialog";

    private const string Recorder =
        "var out = document.getElementById('out'), log = [];" +
        "function show() { out.textContent = log.join('|'); }" +
        "function note(entry) { log.push(entry); show(); }" +
        "function watch(d, name) { ['beforetoggle', 'toggle', 'close', 'cancel'].forEach(function (t) {" +
        "  d.addEventListener(t, function (e) { note(name + ' ' + t + (e.oldState ? ' ' + e.oldState + '>' + e.newState : '') +" +
        "    (e.cancelable ? ' cancelable' : '') + (e.isTrusted ? '' : ' untrusted') + ' open=' + d.open + ' rv=' + d.returnValue); }); }); }";

    private static InteractiveSession Start(string script, string body, IReadOnlyDictionary<string, System.Drawing.RectangleF>? boxes = null)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = boxes is null ? null : () => new DeclaredBoxLayoutView(boxes),
        }));
        var session = engine.ExecuteInteractive(
            [Recorder + script + ";show();"], [], $"<html id=\"root\"><body>{body}<div id=\"out\"></div></body></html>", PageUrl);
        Assert.NotNull(session);
        return session!;
    }

    private static string Settle(string script, string body)
    {
        using var session = Start(script, body);
        return PageProbe.OutOf(session.SettleLoadWindow(), decode: true);
    }

    /// <summary>
    /// A dialog its markup opens is displayed, as the window paints it: the page's scripts read
    /// <c>display: block</c>, and a press on its button is the button's.
    /// </summary>
    [Fact]
    public void ADialogOpenInMarkupIsDisplayedAndPressed()
    {
        using var session = Start(
            "var d = document.getElementById('d'), c = document.getElementById('closed');" +
            "note(getComputedStyle(d).display + ' ' + getComputedStyle(c).display);" +
            "document.getElementById('b').addEventListener('click', function () { note('clicked'); });" +
            "document.addEventListener('pointerdown', function (e) { note('down ' + (e.target.id || e.target.tagName)); });",
            "<dialog id=\"d\" open><button id=\"b\">b</button></dialog><dialog id=\"closed\"><p>x</p></dialog>",
            new Dictionary<string, System.Drawing.RectangleF>
            {
                ["root"] = new(0, 0, 1024, 768),
                ["d"] = new(0, 100, 200, 60),
                ["b"] = new(10, 110, 80, 30),
            });
        session.SettleLoadWindow();

        session.DispatchPointer(new PointerInput(PointerInputKind.Down, 20, 120) { Buttons = 1 });
        session.DispatchPointer(new PointerInput(PointerInputKind.Up, 20, 120));

        Assert.Equal("block none|down b|clicked", PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }

    /// <summary>
    /// <c>show()</c> and <c>close()</c>: <c>beforetoggle</c> at once (cancelable only on the way in), one
    /// <c>toggle</c> task for both, keeping the first old state and the last new one, and <c>close</c> after
    /// the timer queued with them; a second <c>close()</c> changes nothing, not even the return value.
    /// </summary>
    [Fact]
    public void ADialogsEventsComeAsChromiumFiresThem()
    {
        var log = Settle(
            "var d = document.getElementById('d'); watch(d, 'd');" +
            "d.show(); note('after show'); d.close('r1'); note('after close'); d.close('r2'); note('after second close rv=' + d.returnValue);" +
            "Promise.resolve().then(function () { note('microtask'); });" +
            "setTimeout(function () { note('timeout'); }, 0);",
            "<dialog id=\"d\"><p>x</p></dialog>");

        Assert.Equal(
            "d beforetoggle closed>open cancelable open=false rv=|after show|" +
            "d beforetoggle open>closed open=true rv=|after close|after second close rv=r1|microtask|" +
            "d toggle closed>closed open=false rv=r1|timeout|d close open=false rv=r1", log);
    }

    /// <summary>A cancelled <c>beforetoggle</c> keeps the dialog closed, and nothing else fires.</summary>
    [Fact]
    public void ACancelledBeforetoggleKeepsTheDialogClosed()
    {
        var log = Settle(
            "var d = document.getElementById('d'); watch(d, 'd');" +
            "d.addEventListener('beforetoggle', function (e) { e.preventDefault(); });" +
            "d.showModal(); note('open=' + d.open); setTimeout(function () { note('timeout'); }, 0);",
            "<dialog id=\"d\"><p>x</p></dialog>");

        Assert.Equal("d beforetoggle closed>open cancelable open=false rv=|open=false|timeout", log);
    }

    /// <summary>The checks <c>show()</c> and <c>showModal()</c> make, with Chromium's messages.</summary>
    [Fact]
    public void ShowAndShowModalRefuseWhatChromiumRefuses()
    {
        var log = Settle(
            "function attempt(label, f) { try { f(); note(label + ' ok'); } catch (e) { note(label + ' ' + e.name + ': ' + e.message); } }" +
            "var a = document.getElementById('a'), loose = document.createElement('dialog');" +
            "a.show(); attempt('modal on open', function () { a.showModal(); }); a.close();" +
            "a.showModal(); attempt('show on modal', function () { a.show(); }); attempt('modal on modal', function () { a.showModal(); }); a.close();" +
            "attempt('modal loose', function () { loose.showModal(); }); attempt('show loose', function () { loose.show(); }); note('loose open=' + loose.open)",
            "<dialog id=\"a\"><p>x</p></dialog>");

        Assert.Equal(
            "modal on open InvalidStateError: Failed to execute 'showModal' on 'HTMLDialogElement': The dialog is already open as a non-modal dialog, and therefore cannot be opened as a modal dialog.|" +
            "show on modal InvalidStateError: Failed to execute 'show' on 'HTMLDialogElement': The dialog is already open as a modal dialog, and therefore cannot be opened as a non-modal dialog.|" +
            "modal on modal ok|" +
            "modal loose InvalidStateError: Failed to execute 'showModal' on 'HTMLDialogElement': The element is not in a Document.|" +
            "show loose ok|loose open=true", log);
    }

    /// <summary>
    /// <c>requestClose()</c> fires a cancelable <c>cancel</c> first: cancelled, the dialog stays open;
    /// otherwise it closes with the value asked for.
    /// </summary>
    [Fact]
    public void RequestCloseAsksFirst()
    {
        var log = Settle(
            "var d = document.getElementById('d'); watch(d, 'd'); d.showModal();" +
            "d.addEventListener('cancel', function (e) { if (!window.done) { e.preventDefault(); window.done = true; } });" +
            "d.requestClose('first'); note('after first open=' + d.open);" +
            "d.requestClose('second'); note('after second open=' + d.open);",
            "<dialog id=\"d\"><p>x</p></dialog>");

        Assert.Equal(
            "d beforetoggle closed>open cancelable open=false rv=|" +
            "d cancel cancelable open=true rv=|after first open=true|" +
            "d cancel cancelable open=true rv=|d beforetoggle open>closed open=true rv=|after second open=false|" +
            "d toggle closed>closed open=false rv=second|d close open=false rv=second", log);
    }

    /// <summary>Escape asks the modal dialog to close, as Chromium does; a non-modal one stays open.</summary>
    [Fact]
    public void EscapeClosesAModalDialogOnly()
    {
        using var session = Start(
            "var m = document.getElementById('m'), n = document.getElementById('n'); watch(m, 'm'); watch(n, 'n');" +
            "n.show(); m.showModal(); document.getElementById('mb').focus();",
            "<dialog id=\"m\"><button id=\"mb\">m</button></dialog><dialog id=\"n\"><p>n</p></dialog>");
        session.SettleLoadWindow();

        session.DispatchKey(new KeyboardInput(KeyboardInputKind.Down, "Escape", "Escape") { KeyCode = 27 });
        session.DispatchKey(new KeyboardInput(KeyboardInputKind.Up, "Escape", "Escape") { KeyCode = 27 });
        session.SettleLoadWindow();
        session.DispatchKey(new KeyboardInput(KeyboardInputKind.Down, "Escape", "Escape") { KeyCode = 27 });

        Assert.Equal(
            "n beforetoggle closed>open cancelable open=false rv=|m beforetoggle closed>open cancelable open=false rv=|" +
            "n toggle closed>open open=true rv=|m toggle closed>open open=true rv=|" +
            "m cancel cancelable open=true rv=|m beforetoggle open>closed open=true rv=|m toggle open>closed open=false rv=|m close open=false rv=",
            PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }

    /// <summary>A <c>method="dialog"</c> form's button closes its dialog with the button's value, and the dialog hears <c>close</c>.</summary>
    [Fact]
    public void ADialogFormFiresClose()
    {
        var log = Settle(
            "var d = document.getElementById('d'); watch(d, 'd'); d.showModal(); document.getElementById('b').click();",
            "<dialog id=\"d\"><form method=\"dialog\"><button id=\"b\" value=\"v1\">b</button></form></dialog>");

        Assert.Equal(
            "d beforetoggle closed>open cancelable open=false rv=|d beforetoggle open>closed open=true rv=|" +
            "d toggle closed>closed open=false rv=v1|d close open=false rv=v1", log);
    }
}
