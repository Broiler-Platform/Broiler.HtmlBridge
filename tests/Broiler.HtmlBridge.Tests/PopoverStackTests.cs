using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The stacks the open popovers form, as HTML has them now and Chromium does (measured): a hint popover closes only the hint popovers it is not in, an auto popover both stacks, and
/// one shown in a hint popover is a hint one; no popover shows while another of its document shows or hides;
/// a dialog's <c>show()</c> closes the popovers it is not in; and a press or Escape acts on its own document.
/// </summary>
/// <remarks>
/// <b>A hint popover was an auto one</b>: showing it closed every open auto popover, and showing an auto one
/// in it closed it. A listener could show a popover while another showed or hid, which Chromium refuses with
/// an <c>InvalidStateError</c>; and only <c>showModal()</c> closed the open popovers, not <c>show()</c>.
/// </remarks>
public class PopoverStackTests
{
    private const string PageUrl = "https://example.test/popover-stack";

    private const string Recorder =
        "var out = document.getElementById('out'), log = [];" +
        "function show() { out.textContent = log.join('|'); }" +
        "function note(entry) { log.push(entry); show(); }" +
        "function open() { return Array.from(document.querySelectorAll(':popover-open')).map(function (e) { return e.id; }).join(','); }" +
        "function watchAll(events) { Array.from(document.querySelectorAll('[popover]')).forEach(function (p) { events.forEach(function (t) {" +
        "  p.addEventListener(t, function (e) { note((t === 'beforetoggle' ? 'bt ' : t + ' ') + p.id + ' ' + e.oldState + '>' + e.newState); }); }); }); }" +
        "function step(name, f) { log = []; try { f(); } catch (e) { note('ERR ' + e.name + ': ' + e.message); } note(name + ' => ' + open()); }" +
        "function reset() { Array.from(document.querySelectorAll(':popover-open')).forEach(function (e) { e.hidePopover(); }); }";

    private static InteractiveSession Start(string script, string body, IReadOnlyDictionary<string, System.Drawing.RectangleF>? boxes = null)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = boxes is null ? null : () => new DeclaredFrameLayoutView(boxes),
        }));
        var session = engine.ExecuteInteractive(
            [Recorder + script + ";show();"], [], $"<html id=\"root\"><body id=\"body\" style=\"margin:0\">{body}<div id=\"out\"></div></body></html>", PageUrl);
        Assert.NotNull(session);
        return session!;
    }

    /// <summary>Runs <paramref name="steps"/>, each ending its entries with what is open, and answers the entries of every step.</summary>
    private static string Steps(string steps, string body)
    {
        using var session = Start(
            "watchAll(['beforetoggle']); var all = [];" +
            "function record(name, f) { step(name, f); all.push(log.join('|')); }" + steps +
            ";show = function () { out.textContent = all.join(' ## '); };",
            body);
        session.SettleLoadWindow();
        return PageProbe.OutOf(session.SettleLoadWindow(), decode: true);
    }

    private const string StackPage =
        "<div popover id=\"A\">A <div popover=\"hint\" id=\"HinA\">HinA</div></div><div popover id=\"B\">B</div>" +
        "<div popover=\"hint\" id=\"H1\">H1 <div popover=\"hint\" id=\"H1in\">H1in</div><div popover id=\"AinH1\">AinH1</div></div>" +
        "<div popover=\"hint\" id=\"H2\">H2</div><div popover=\"manual\" id=\"M\">M</div>";

    /// <summary>
    /// Chromium's sequence: a hint popover leaves an auto one open, an auto one closes the hint ones first,
    /// a hint one in a hint one stays above it, an unrelated hint one closes the hint stack, an auto popover
    /// in a hint one leaves it open and is a hint one -- an unrelated hint one closes both --, a hint one in an
    /// auto one goes with it, and one beside it does not.
    /// </summary>
    [Fact]
    public void HintPopoversAreAStackOfTheirOwn()
    {
        var log = Steps(
            "function $(id) { return document.getElementById(id); }" +
            "record('1', function () { $('A').showPopover(); $('H2').showPopover(); });" +
            "record('2', function () { $('B').showPopover(); });" +
            "record('3', function () { $('H1').showPopover(); });" +
            "record('4', function () { $('H1in').showPopover(); });" +
            "record('5', function () { $('H2').showPopover(); });" +
            "record('6', function () { reset(); log = []; $('H1').showPopover(); $('AinH1').showPopover(); });" +
            "record('6b', function () { $('H2').showPopover(); });" +
            "record('7', function () { reset(); log = []; $('A').showPopover(); $('HinA').showPopover(); });" +
            "record('8', function () { $('A').hidePopover(); });" +
            "record('9', function () { $('A').showPopover(); $('H2').showPopover(); log = []; $('A').hidePopover(); });" +
            "record('10', function () { reset(); log = []; $('M').showPopover(); $('H2').showPopover(); log = []; $('A').showPopover(); });",
            StackPage);

        Assert.Equal(
            "bt A closed>open|bt H2 closed>open|1 => A,H2 ## " +
            "bt B closed>open|bt H2 open>closed|bt A open>closed|2 => B ## " +
            "bt H1 closed>open|3 => B,H1 ## " +
            "bt H1in closed>open|4 => B,H1,H1in ## " +
            "bt H2 closed>open|bt H1in open>closed|bt H1 open>closed|5 => B,H2 ## " +
            "bt H1 closed>open|bt AinH1 closed>open|6 => H1,AinH1 ## " +
            "bt H2 closed>open|bt AinH1 open>closed|bt H1 open>closed|6b => H2 ## " +
            "bt A closed>open|bt HinA closed>open|7 => A,HinA ## " +
            "bt HinA open>closed|bt A open>closed|8 =>  ## " +
            "bt A open>closed|9 => H2 ## " +
            "bt A closed>open|bt H2 open>closed|10 => A,M", log);
    }

    /// <summary>
    /// A dialog's <c>show()</c>, like its <c>showModal()</c>, closes the auto and hint popovers it is not in,
    /// the topmost first, and leaves a manual one and the one it is in open.
    /// </summary>
    [Fact]
    public void ADialogClosesThePopoversItIsNotIn()
    {
        var log = Steps(
            "function $(id) { return document.getElementById(id); }" +
            "record('show', function () { $('A').showPopover(); $('H').showPopover(); $('M').showPopover(); log = []; $('d').show(); });" +
            "record('show in A', function () { $('d').close(); $('A').showPopover(); log = []; $('din').show(); });" +
            "record('modal', function () { $('din').close(); reset(); $('M').showPopover(); $('A').showPopover(); $('HinA').showPopover(); log = []; $('d').showModal(); });",
            "<div popover id=\"A\">A<dialog id=\"din\">in A</dialog><div popover=\"hint\" id=\"HinA\">HinA</div></div>" +
            "<div popover=\"hint\" id=\"H\">H</div><div popover=\"manual\" id=\"M\">M</div><dialog id=\"d\">d</dialog>");

        Assert.Equal(
            "bt H open>closed|bt A open>closed|show => M ## " +
            "show in A => A,M ## " +
            "bt HinA open>closed|bt A open>closed|modal => M", log);
    }

    /// <summary>
    /// No popover shows while another of the document shows or hides -- Chromium's <c>InvalidStateError</c>,
    /// for <c>togglePopover()</c> too -- though one may hide; a hide a popover's own <c>beforetoggle</c> asks for
    /// finishes it at once, with no events of its own; and a popover whose attribute changed, or that left its
    /// document, while its show ran is not shown, with Chromium's messages.
    /// </summary>
    [Fact]
    public void NoPopoverShowsWhileAnotherShowsOrHides()
    {
        var log = Steps(
            "function $(id) { return document.getElementById(id); }" +
            "var hook = null;" +
            "Array.from(document.querySelectorAll('[popover]')).forEach(function (p) { p.addEventListener('beforetoggle', function (e) {" +
            "  if (hook && hook.el === p && hook.state === e.newState) { var h = hook; hook = null;" +
            "    try { h.f(); note('inner ok open=' + p.matches(':popover-open')); } catch (err) { note('inner ' + err.name + ': ' + err.message); } } }); });" +
            "record('during show', function () { hook = { el: $('X'), state: 'open', f: function () { $('Y').showPopover(); } }; $('X').showPopover(); });" +
            "record('manual during show', function () { reset(); log = []; hook = { el: $('X'), state: 'open', f: function () { $('Z').showPopover(); } }; $('X').showPopover(); });" +
            "record('during hide', function () { log = []; hook = { el: $('X'), state: 'closed', f: function () { $('Y').showPopover(); } }; $('X').hidePopover(); });" +
            "record('hide during show', function () { $('Y').showPopover(); log = []; hook = { el: $('X'), state: 'open', f: function () { $('Y').hidePopover(); } }; $('X').showPopover(); });" +
            "record('toggle during hide', function () { log = []; hook = { el: $('X'), state: 'closed', f: function () { $('Y').togglePopover(); } }; $('X').hidePopover(); });" +
            "record('nested hide', function () { $('X').showPopover(); log = []; hook = { el: $('X'), state: 'closed', f: function () { $('X').hidePopover(); } }; $('X').hidePopover(); });" +
            "record('type changed', function () { $('Y').showPopover(); log = []; hook = { el: $('Y'), state: 'closed', f: function () { $('X').popover = 'manual'; } }; $('X').showPopover(); });" +
            "record('removed', function () { reset(); log = []; hook = { el: $('Z'), state: 'open', f: function () { $('Z').remove(); } }; $('Z').showPopover(); });",
            "<div popover id=\"X\">X</div><div popover id=\"Y\">Y</div><div popover=\"manual\" id=\"Z\">Z</div>");

        const string During = "Invalid to show a popover during another show operation";
        Assert.Equal(
            $"bt X closed>open|inner InvalidStateError: Failed to execute 'showPopover' on 'HTMLElement': {During}|during show => X ## " +
            $"bt X closed>open|inner InvalidStateError: Failed to execute 'showPopover' on 'HTMLElement': {During}|manual during show => X ## " +
            $"bt X open>closed|inner InvalidStateError: Failed to execute 'showPopover' on 'HTMLElement': {During}|during hide =>  ## " +
            "bt X closed>open|bt Y open>closed|inner ok open=false|hide during show => X ## " +
            $"bt X open>closed|inner InvalidStateError: Failed to execute 'togglePopover' on 'HTMLElement': {During}|toggle during hide =>  ## " +
            "bt X open>closed|inner ok open=false|nested hide =>  ## " +
            "bt X closed>open|bt Y open>closed|inner ok open=true|" +
            "ERR InvalidStateError: Failed to execute 'showPopover' on 'HTMLElement': The popover attribute changed while hiding other popovers.|type changed =>  ## " +
            "bt Z closed>open|inner ok open=false|" +
            "ERR InvalidStateError: Failed to execute 'showPopover' on 'HTMLElement': Invalid on disconnected popover elements. " +
            "This might have been the result of the \"beforetoggle\" event handler changing the state of this popover.|removed => ",
            log);
    }

    /// <summary>A hide a popover's own <c>beforetoggle</c> asks for queues no <c>toggle</c>: the one its show queued fires, closed to open (measured).</summary>
    [Fact]
    public void ANestedHideQueuesNoToggle()
    {
        using var session = Start(
            "watchAll(['toggle']); var x = document.getElementById('X'), once = true;" +
            "x.addEventListener('beforetoggle', function (e) { if (e.newState === 'closed' && once) { once = false; x.hidePopover(); } });" +
            "x.showPopover(); x.hidePopover(); note('open=' + x.matches(':popover-open'));",
            "<div popover id=\"X\">X</div>");

        Assert.Equal("open=false|toggle X closed>open", PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }

    private static readonly Dictionary<string, System.Drawing.RectangleF> StackBoxes = new()
    {
        ["root"] = new(0, 0, 1024, 768),
        ["body"] = new(0, 0, 1024, 768),
        ["A"] = new(50, 50, 304, 204),
        ["HinA"] = new(400, 50, 204, 104),
        ["B"] = new(50, 300, 204, 104),
        ["H"] = new(400, 300, 204, 104),
        ["outside"] = new(650, 450, 150, 60),
    };

    private const string ClickPage =
        "<div popover id=\"A\">A<div popover=\"hint\" id=\"HinA\">HinA</div></div><div popover id=\"B\">B</div>" +
        "<div popover=\"hint\" id=\"H\">H</div><div id=\"outside\">outside</div>";

    /// <summary>
    /// A press in a hint popover keeps it and the auto popover it was shown in; a press in that auto popover
    /// closes the hint one; a press in a hint popover shown in none closes the open auto popovers, and a press
    /// in one of those closes the hint one -- each after the press's <c>mousedown</c> (measured).
    /// </summary>
    [Fact]
    public void APressClosesThePopoversItIsNotIn()
    {
        using var session = Start(
            "watchAll(['beforetoggle']);" +
            "['mousedown', 'pointerup'].forEach(function (t) { document.addEventListener(t, function (e) { note(t + ' ' + e.target.id); }); });" +
            "document.getElementById('A').showPopover(); document.getElementById('HinA').showPopover(); log = [];",
            ClickPage, StackBoxes);
        session.SettleLoadWindow();

        Assert.Equal("mousedown HinA|pointerup HinA", Click(session, 500, 100));
        Assert.Equal("mousedown A|bt HinA open>closed|pointerup A", Click(session, 100, 100));

        Run(session, "document.getElementById('A').hidePopover(); document.getElementById('B').showPopover(); document.getElementById('H').showPopover();");
        Assert.Equal("mousedown H|bt B open>closed|pointerup H", Click(session, 500, 350));

        Run(session, "document.getElementById('H').hidePopover(); document.getElementById('B').showPopover(); document.getElementById('H').showPopover();");
        Assert.Equal("mousedown B|bt H open>closed|pointerup B", Click(session, 100, 350));
    }

    /// <summary>Escape closes the topmost hint popover first, then the auto one it was shown in; a hint popover beside an auto one goes alone (measured).</summary>
    [Fact]
    public void EscapeClosesTheTopmostHintPopoverFirst()
    {
        using var session = Start(
            "watchAll(['beforetoggle']);" +
            "document.getElementById('A').showPopover(); document.getElementById('HinA').showPopover(); log = [];",
            ClickPage);
        session.SettleLoadWindow();

        Assert.Equal("bt HinA open>closed", Escape(session));
        Assert.Equal("bt A open>closed", Escape(session));

        Run(session, "document.getElementById('B').showPopover(); document.getElementById('H').showPopover();");
        Assert.Equal("bt H open>closed", Escape(session));
        Run(session, "log = []; note(open());");
        Assert.Equal("B", PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }

    /// <summary>
    /// A press or Escape in a frame leaves the page's popover open, and a press or Escape in the page leaves
    /// the frame's (measured: each document's light dismiss and close requests are its own).
    /// </summary>
    [Fact]
    public void EachDocumentClosesOnlyItsOwnPopovers()
    {
        using var session = Start(
            "watchAll(['beforetoggle']);" +
            "document.getElementById('P').showPopover(); log = [];",
            "<div popover id=\"P\">P</div><div id=\"plain\">plain</div>" +
            "<iframe id=\"fr\" srcdoc=\"<body id=fbody><div popover id=Q>Q</div><div id=fplain>frame</div></body>\"></iframe>",
            new Dictionary<string, System.Drawing.RectangleF>
            {
                ["root"] = new(0, 0, 1024, 768),
                ["body"] = new(0, 0, 1024, 768),
                ["P"] = new(600, 20, 200, 80),
                ["plain"] = new(600, 500, 200, 60),
                ["fr"] = new(20, 150, 500, 300),
                ["fbody"] = new(20, 150, 500, 300),
                ["Q"] = new(270, 160, 200, 80),
                ["fplain"] = new(30, 300, 200, 60),
            });
        session.SettleLoadWindow();

        Click(session, 100, 330);
        Escape(session);
        Run(session, "var q = document.getElementById('fr').contentDocument.getElementById('Q');" +
                     "q.addEventListener('beforetoggle', function (e) { note('bt Q ' + e.oldState + '>' + e.newState); }); q.showPopover(); log = [];" +
                     "note('frame press P=' + document.getElementById('P').matches(':popover-open'));");
        Assert.Equal("frame press P=true", PageProbe.OutOf(session.SettleLoadWindow(), decode: true));

        Assert.Equal("bt P open>closed", Click(session, 700, 530));
        Assert.Equal(string.Empty, Escape(session));
        Run(session, "log = []; note('Q=' + document.getElementById('fr').contentDocument.getElementById('Q').matches(':popover-open'));");
        Assert.Equal("Q=true", PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }

    private static string Click(InteractiveSession session, double x, double y)
    {
        Run(session, "log = [];");
        session.DispatchPointer(new PointerInput(PointerInputKind.Down, x, y) { Buttons = 1 });
        session.DispatchPointer(new PointerInput(PointerInputKind.Up, x, y));
        return PageProbe.OutOf(session.SettleLoadWindow(), decode: true);
    }

    private static string Escape(InteractiveSession session)
    {
        Run(session, "log = [];");
        session.DispatchKey(new KeyboardInput(KeyboardInputKind.Down, "Escape", "Escape") { KeyCode = 27 });
        session.DispatchKey(new KeyboardInput(KeyboardInputKind.Up, "Escape", "Escape") { KeyCode = 27 });
        return PageProbe.OutOf(session.SettleLoadWindow(), decode: true);
    }

    private static void Run(InteractiveSession session, string script)
    {
        session.RunJavaScriptUrl("javascript:void (function () { " + script + " show(); })()");
        session.SettleLoadWindow();
    }
}
