using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The popover API as Chromium has it: <c>showPopover()</c>, <c>hidePopover()</c> and <c>togglePopover()</c>
/// on every HTML element, with their <c>beforetoggle</c> and <c>toggle</c>; auto popovers that close one
/// another; an invoker's <c>popovertarget</c>; light dismiss and Escape; and a closed popover that is not
/// displayed.
/// </summary>
/// <remarks>
/// <para>
/// <b>A popover changed and nobody heard</b>: its two methods existed only on an element that had the
/// attribute when its wrapper was made, fired nothing and knew no other popover; there was no
/// <c>togglePopover()</c>, no <c>popover</c> property, no invoker, no light dismiss, and a closed popover
/// was displayed in the flow, since <c>:popover-open</c> matched every element.
/// </para>
/// <para>Measured in Chromium.</para>
/// </remarks>
public class PopoverTests
{
    private const string PageUrl = "https://example.test/popover";

    private const string Recorder =
        "var out = document.getElementById('out'), log = [];" +
        "function show() { out.textContent = log.join('|'); }" +
        "function note(entry) { log.push(entry); show(); }" +
        "function attempt(label, f) { try { var v = f(); note(label + ' ok ' + v); } catch (e) { note(label + ' ' + e.name + ': ' + e.message); } }" +
        "function watch(p) { ['beforetoggle', 'toggle'].forEach(function (t) {" +
        "  p.addEventListener(t, function (e) { note(p.id + ' ' + t + ' ' + e.oldState + '>' + e.newState + (e.cancelable ? ' cancelable' : '') +" +
        "    (e.bubbles ? ' bubbles' : '') + (e.isTrusted ? '' : ' untrusted') + ' ' + e.constructor.name + ' open=' + p.matches(':popover-open')); }); }); }";

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
    /// <c>showPopover()</c> fires a cancelable <c>beforetoggle</c> before the popover opens, a second one does
    /// nothing, <c>hidePopover()</c> fires one that cannot be cancelled; <c>toggle</c> follows as a task.
    /// </summary>
    [Fact]
    public void APopoverOpensAndClosesWithItsEvents()
    {
        var log = Settle(
            "var p = document.getElementById('p'); watch(p);" +
            "p.showPopover(); note('after show open=' + p.matches(':popover-open')); p.showPopover(); note('again');" +
            "setTimeout(function () { p.hidePopover(); note('after hide'); p.hidePopover(); note('again'); }, 0);",
            "<div id=\"p\" popover>p</div>");

        Assert.Equal(
            "p beforetoggle closed>open cancelable ToggleEvent open=false|after show open=true|again|" +
            "p toggle closed>open ToggleEvent open=true|" +
            "p beforetoggle open>closed ToggleEvent open=true|after hide|again|p toggle open>closed ToggleEvent open=false", log);
    }

    /// <summary>
    /// A show and a hide in one task are one <c>toggle</c>, closed to closed; a cancelled <c>beforetoggle</c>
    /// keeps the popover closed; <c>togglePopover()</c> answers the state it leaves, as <c>force</c> allows.
    /// </summary>
    [Fact]
    public void TogglesCoalesceAndTogglePopoverAnswersTheState()
    {
        var log = Settle(
            "var p = document.getElementById('p'); watch(p);" +
            "p.showPopover(); p.hidePopover();" +
            "setTimeout(function () {" +
            "  p.addEventListener('beforetoggle', function (e) { if (e.newState === 'open') e.preventDefault(); }, { once: true });" +
            "  p.showPopover(); note('cancelled open=' + p.matches(':popover-open'));" +
            "  note('toggle() ' + p.togglePopover()); note('toggle(true) ' + p.togglePopover(true)); note('force false ' + p.togglePopover({ force: false }));" +
            "}, 0);",
            "<div id=\"p\" popover>p</div>");

        Assert.Equal(
            "p beforetoggle closed>open cancelable ToggleEvent open=false|p beforetoggle open>closed ToggleEvent open=true|" +
            "p toggle closed>closed ToggleEvent open=false|" +
            "p beforetoggle closed>open cancelable ToggleEvent open=false|cancelled open=false|" +
            "p beforetoggle closed>open cancelable ToggleEvent open=false|toggle() true|toggle(true) true|" +
            "p beforetoggle open>closed ToggleEvent open=true|force false false|p toggle closed>closed ToggleEvent open=false", log);
    }

    /// <summary>
    /// Showing an auto popover fires its own <c>beforetoggle</c> first, then closes the auto popovers it is
    /// not in, the innermost first; a manual one stays. Each <c>toggle</c> a second change coalesced went
    /// to the back of the queue: pm, p1c, p1, p2 (measured).
    /// </summary>
    [Fact]
    public void AnAutoPopoverClosesTheOthers()
    {
        var log = Settle(
            "var p1 = document.getElementById('p1'), p1c = document.getElementById('p1c'), pm = document.getElementById('pm'), p2 = document.getElementById('p2');" +
            "[p1, p1c, pm, p2].forEach(watch);" +
            "p1.showPopover(); p1c.showPopover(); pm.showPopover(); note('nested open p1=' + p1.matches(':popover-open'));" +
            "p2.showPopover(); note('p1=' + p1.matches(':popover-open') + ' p1c=' + p1c.matches(':popover-open') + ' pm=' + pm.matches(':popover-open'));",
            "<div id=\"p1\" popover>one<div id=\"p1c\" popover>child</div></div><div id=\"pm\" popover=\"manual\">m</div><div id=\"p2\" popover=\"auto\">two</div>");

        Assert.Equal(
            "p1 beforetoggle closed>open cancelable ToggleEvent open=false|p1c beforetoggle closed>open cancelable ToggleEvent open=false|" +
            "pm beforetoggle closed>open cancelable ToggleEvent open=false|nested open p1=true|" +
            "p2 beforetoggle closed>open cancelable ToggleEvent open=false|p1c beforetoggle open>closed ToggleEvent open=true|" +
            "p1 beforetoggle open>closed ToggleEvent open=true|p1=false p1c=false pm=true|" +
            "pm toggle closed>open ToggleEvent open=true|p1c toggle closed>closed ToggleEvent open=false|" +
            "p1 toggle closed>closed ToggleEvent open=false|p2 toggle closed>open ToggleEvent open=true", log);
    }

    /// <summary>A dialog's coalesced <c>toggle</c> goes behind a timer queued between the two changes, as a popover's does (measured).</summary>
    [Fact]
    public void ACoalescedToggleGoesToTheBackOfTheQueue()
    {
        var log = Settle(
            "var d = document.getElementById('d'), d2 = document.getElementById('d2');" +
            "[d, d2].forEach(function (x) { x.addEventListener('toggle', function (e) { note('toggle ' + x.id + ' ' + e.oldState + '>' + e.newState); }); });" +
            "d.show(); setTimeout(function () { note('timeout A'); }, 0); d2.show(); d.close(); setTimeout(function () { note('timeout B'); }, 0);",
            "<dialog id=\"d\">d</dialog><dialog id=\"d2\">d2</dialog>");

        Assert.Equal("timeout A|toggle d2 closed>open|toggle d closed>closed|timeout B", log);
    }

    /// <summary>The checks the three methods make, with Chromium's messages; and the <c>popover</c> property.</summary>
    [Fact]
    public void PopoversRefuseWhatChromiumRefuses()
    {
        var log = Settle(
            "var plain = document.getElementById('plain'), loose = document.createElement('div'); loose.popover = 'auto';" +
            "attempt('show plain', function () { plain.showPopover(); }); attempt('hide plain', function () { plain.hidePopover(); });" +
            "attempt('toggle plain', function () { plain.togglePopover(); });" +
            "attempt('show loose', function () { loose.showPopover(); }); attempt('hide loose', function () { loose.hidePopover(); });" +
            "var d = document.getElementById('dp'); d.showModal(); attempt('show modal', function () { d.showPopover(); }); d.close();" +
            "d.showPopover(); attempt('modal on popover', function () { d.showModal(); }); d.hidePopover();" +
            "note(['plain', 'a', 'e', 'm', 'h', 'b'].map(function (id) { return document.getElementById(id).popover; }).join(',') + ' ' + ('showPopover' in HTMLElement.prototype) + ' ' + ('togglePopover' in HTMLElement.prototype));" +
            "plain.popover = 'manual'; note(plain.getAttribute('popover')); plain.popover = null; note(plain.hasAttribute('popover'));",
            "<div id=\"plain\">x</div><dialog id=\"dp\" popover>d</dialog><div id=\"a\" popover=\"auto\">a</div><div id=\"e\" popover=\"\">e</div>" +
            "<div id=\"m\" popover=\"manual\">m</div><div id=\"h\" popover=\"hint\">h</div><div id=\"b\" popover=\"bogus\">b</div>");

        Assert.Equal(
            "show plain NotSupportedError: Failed to execute 'showPopover' on 'HTMLElement': Not supported on elements that are not popovers.|" +
            "hide plain NotSupportedError: Failed to execute 'hidePopover' on 'HTMLElement': Not supported on elements that are not popovers. This might have been the result of the \"beforetoggle\" event handler changing the state of this popover.|" +
            "toggle plain NotSupportedError: Failed to execute 'togglePopover' on 'HTMLElement': Not supported on elements that are not popovers.|" +
            "show loose InvalidStateError: Failed to execute 'showPopover' on 'HTMLElement': Invalid on disconnected popover elements.|" +
            "hide loose ok undefined|" +
            "show modal InvalidStateError: Failed to execute 'showPopover' on 'HTMLElement': The dialog is already open as a dialog, and therefore cannot be opened as a popover.|" +
            "modal on popover InvalidStateError: Failed to execute 'showModal' on 'HTMLDialogElement': The dialog is already open as a Popover, and therefore cannot be opened as a modal dialog.|" +
            ",auto,auto,manual,hint,manual true true|manual|false", log);
    }

    /// <summary>
    /// A closed popover is not displayed -- unless an author gives it a display of their own, the rule being the
    /// user agent's -- and an open one is; <c>:popover-open</c> matches the open one alone.
    /// </summary>
    [Fact]
    public void AClosedPopoverIsNotDisplayed()
    {
        var log = Settle(
            "var p = document.getElementById('p'), s = document.getElementById('s');" +
            "note(getComputedStyle(p).display + ' ' + getComputedStyle(s).display + ' ' + document.querySelectorAll(':popover-open').length);" +
            "p.showPopover(); note(getComputedStyle(p).display + ' ' + Array.from(document.querySelectorAll(':popover-open')).map(function (x) { return x.id; }).join(','));" +
            "p.hidePopover(); note(getComputedStyle(p).display + ' ' + document.body.matches(':popover-open') + ' ' + document.body.matches(':modal'));",
            "<style>#s { display: flex }</style><div id=\"p\" popover>p</div><div id=\"s\" popover=\"manual\">s</div>");

        Assert.Equal("none flex 0|block p|none false false", log);
    }

    /// <summary>
    /// A click on a <c>popovertarget</c> button opens its popover once the click is dispatched; a click on it
    /// again closes it, with no light dismiss between; <c>popovertargetaction</c> limits it to one way.
    /// </summary>
    [Fact]
    public void AnInvokerTogglesItsPopover()
    {
        using var session = Start(
            "var p = document.getElementById('p'); watch(p);" +
            "['pointerdown', 'mousedown', 'pointerup', 'mouseup', 'click'].forEach(function (t) { document.addEventListener(t, function (e) { note(t + ' ' + e.target.id); }); });" +
            "note(document.getElementById('b').popoverTargetElement.id + ' ' + document.getElementById('b').popoverTargetAction + ' ' + document.getElementById('h').popoverTargetAction);",
            "<button id=\"b\" popovertarget=\"p\">b</button><button id=\"h\" popovertarget=\"p\" popovertargetaction=\"hide\">h</button><div id=\"p\" popover>p</div>",
            new Dictionary<string, System.Drawing.RectangleF>
            {
                ["root"] = new(0, 0, 1024, 768),
                ["body"] = new(0, 0, 1024, 768),
                ["b"] = new(10, 10, 80, 30),
                ["h"] = new(10, 50, 80, 30),
            });
        session.SettleLoadWindow();

        Click(session, 20, 20);
        session.SettleLoadWindow();
        Click(session, 20, 20);
        session.SettleLoadWindow();
        Click(session, 20, 60);

        Assert.Equal(
            "p toggle hide|pointerdown b|mousedown b|pointerup b|mouseup b|click b|p beforetoggle closed>open cancelable ToggleEvent open=false|" +
            "p toggle closed>open ToggleEvent open=true|" +
            "pointerdown b|mousedown b|pointerup b|mouseup b|click b|p beforetoggle open>closed ToggleEvent open=true|p toggle open>closed ToggleEvent open=false|" +
            "pointerdown h|mousedown h|pointerup h|mouseup h|click h",
            PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }

    /// <summary>
    /// A press outside the open auto popover closes it after the press's <c>mousedown</c>, before its
    /// <c>pointerup</c>; a press in it does not; a manual one stays open (measured).
    /// </summary>
    [Fact]
    public void APressOutsideClosesTheOpenPopovers()
    {
        using var session = Start(
            "var p = document.getElementById('p'), m = document.getElementById('m'); watch(p);" +
            "['pointerdown', 'mousedown', 'pointerup', 'mouseup', 'click'].forEach(function (t) { document.addEventListener(t, function (e) { note(t + ' ' + e.target.id); }); });" +
            "p.showPopover(); m.showPopover();",
            "<div id=\"outside\">outside</div><div id=\"p\" popover>p</div><div id=\"m\" popover=\"manual\">m</div>",
            new Dictionary<string, System.Drawing.RectangleF>
            {
                ["root"] = new(0, 0, 1024, 768),
                ["body"] = new(0, 0, 1024, 768),
                ["outside"] = new(10, 600, 200, 40),
                ["p"] = new(400, 300, 200, 100),
                ["m"] = new(400, 450, 200, 60),
            });
        session.SettleLoadWindow();

        Click(session, 450, 320);
        Click(session, 50, 610);
        session.SettleLoadWindow();

        Assert.Equal(
            "p beforetoggle closed>open cancelable ToggleEvent open=false|p toggle closed>open ToggleEvent open=true|" +
            "pointerdown p|mousedown p|pointerup p|mouseup p|click p|" +
            "pointerdown outside|mousedown outside|p beforetoggle open>closed ToggleEvent open=true|pointerup outside|mouseup outside|click outside|" +
            "p toggle open>closed ToggleEvent open=false",
            PageProbe.OutOf(session.SettleLoadWindow(), decode: true));

        session.RunJavaScriptUrl("javascript:void (log = [], note('manual open=' + m.matches(':popover-open')))");
        Assert.Equal("manual open=true", PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }

    /// <summary>Escape closes the topmost auto popover after its <c>keydown</c>, before its <c>keyup</c> (measured).</summary>
    [Fact]
    public void EscapeClosesTheTopmostPopover()
    {
        using var session = Start(
            "var p = document.getElementById('p'), q = document.getElementById('q'); watch(p); watch(q);" +
            "['keydown', 'keyup'].forEach(function (t) { document.addEventListener(t, function (e) { note(t + ' ' + e.key); }); });" +
            "p.showPopover(); q.showPopover();",
            "<div id=\"p\" popover>p<div id=\"q\" popover>q</div></div>");
        session.SettleLoadWindow();

        session.DispatchKey(new KeyboardInput(KeyboardInputKind.Down, "Escape", "Escape") { KeyCode = 27 });
        session.DispatchKey(new KeyboardInput(KeyboardInputKind.Up, "Escape", "Escape") { KeyCode = 27 });

        Assert.EndsWith(
            "keydown Escape|q beforetoggle open>closed ToggleEvent open=true|keyup Escape|q toggle open>closed ToggleEvent open=false",
            PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }

    /// <summary>
    /// A showing popover taken out of its document closes with no events; one whose <c>popover</c> attribute
    /// goes closes with them; <c>showModal()</c> closes the open auto popovers, not a manual one (measured).
    /// </summary>
    [Fact]
    public void RemovalAttributesAndModalDialogsClosePopovers()
    {
        var log = Settle(
            "var p = document.getElementById('p'), q = document.getElementById('q'), m = document.getElementById('m'), d = document.getElementById('d');" +
            "p.showPopover(); p.remove(); note('removed ' + p.matches(':popover-open')); document.body.appendChild(p); note('back ' + p.matches(':popover-open'));" +
            "watch(q); q.showPopover(); q.removeAttribute('popover'); note('attribute gone ' + q.matches(':popover-open'));" +
            "watch(m); var r = document.getElementById('r'); watch(r); r.showPopover(); m.showPopover(); d.showModal(); note('modal r=' + r.matches(':popover-open') + ' m=' + m.matches(':popover-open'));",
            "<div id=\"p\" popover>p</div><div id=\"q\" popover>q</div><div id=\"r\" popover>r</div><div id=\"m\" popover=\"manual\">m</div><dialog id=\"d\">d</dialog>");

        Assert.Equal(
            "removed false|back false|" +
            "q beforetoggle closed>open cancelable ToggleEvent open=false|q beforetoggle open>closed ToggleEvent open=true|attribute gone false|" +
            "r beforetoggle closed>open cancelable ToggleEvent open=false|m beforetoggle closed>open cancelable ToggleEvent open=false|" +
            "r beforetoggle open>closed ToggleEvent open=true|modal r=false m=true|" +
            "q toggle closed>closed ToggleEvent open=false|m toggle closed>open ToggleEvent open=true|r toggle closed>closed ToggleEvent open=false", log);
    }

    private static void Click(InteractiveSession session, double x, double y)
    {
        session.DispatchPointer(new PointerInput(PointerInputKind.Down, x, y) { Buttons = 1 });
        session.DispatchPointer(new PointerInput(PointerInputKind.Up, x, y));
    }
}
