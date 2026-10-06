using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// <c>PointerEvent</c> and pointer capture: the pointer events and a click are <c>PointerEvent</c>s, and an
/// element that captures the pointer gets the gesture's events wherever the pointer goes, as in Chromium.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neither existed.</b> <c>PointerEvent</c> was undefined, so pages took their mouse-event paths, and
/// <c>setPointerCapture</c> was undefined, so a page that drags by capturing the pointer threw in its
/// <c>pointerdown</c> listener.
/// </para>
/// <para>
/// Measured in Chromium with a trusted click: <c>pointerdown</c> (whose listener captures; it reads
/// <c>hasPointerCapture</c> true at once), <c>mousedown</c>, <c>gotpointercapture</c>, <c>pointerup</c>,
/// <c>mouseup</c>, <c>lostpointercapture</c>, <c>click</c>; the pointer events and the click are
/// <c>PointerEvent</c>s with pointer 1 of type <c>mouse</c>, the mouse events <c>MouseEvent</c>s. An unknown
/// pointer id throws <c>NotFoundError</c>; with no button down nothing is captured. The layout is declared
/// (<see cref="DeclaredFrameLayoutView"/>).
/// </para>
/// </remarks>
public class PointerCaptureTests
{
    private const string PageUrl = "https://example.test/capture";

    private static readonly Dictionary<string, System.Drawing.RectangleF> PageBoxes = new()
    {
        ["pc"] = new(10, 10, 200, 120),
        ["other"] = new(300, 10, 100, 100),
    };

    private const string Body = "<div id=\"pc\">pc</div><div id=\"other\">other</div>";

    private const string Recorder =
        "var log = [], pageOut = document.getElementById('out');" +
        "function note(entry) { log.push(entry); pageOut.textContent = log.join(' '); }" +
        "['pointerdown', 'mousedown', 'pointerup', 'mouseup', 'click', 'gotpointercapture', 'lostpointercapture', 'pointermove', 'mousemove', 'pointerover', 'pointerout']" +
        ".forEach(function (type) { window.addEventListener(type, function (e) {" +
        " note(type + ':' + (e.target.id || e.target.nodeName.toLowerCase()) + (e instanceof PointerEvent ? ':P' : e instanceof MouseEvent ? ':M' : ':?')); }, true); });";

    private static InteractiveSession Start(string script)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = () => new DeclaredFrameLayoutView(PageBoxes),
        }));

        var session = engine.ExecuteInteractive(
            [Recorder + script], [],
            $"<html><body style=\"margin:0\">{Body}<div id=\"out\"></div></body></html>",
            PageUrl);
        Assert.NotNull(session);
        session!.SettleLoadWindow();
        return session;
    }

    /// <summary>Reads what the page recorded since the last read.</summary>
    private sealed class Recording(InteractiveSession session)
    {
        private int _read;

        public string After(PointerInput input)
        {
            session.DispatchPointer(input);
            return Read();
        }

        public string Read()
        {
            var all = PageProbe.OutOf(session.CurrentHtml(), decode: true).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var next = string.Join(" ", all.Skip(_read));
            _read = all.Length;
            return next;
        }
    }

    private const string CaptureOnPress =
        "document.getElementById('pc').addEventListener('pointerdown', function (e) {" +
        " this.setPointerCapture(e.pointerId); note('captured ' + this.hasPointerCapture(e.pointerId) + ' ' + e.pointerId + ' ' + e.pointerType); });";

    /// <summary>A click whose <c>pointerdown</c> captures the pointer: Chromium's order, and its interfaces.</summary>
    [Fact]
    public void ACaptureIsGotBeforeTheReleaseAndLostBeforeTheClick()
    {
        using var session = Start(CaptureOnPress);
        var recording = new Recording(session);

        recording.After(new PointerInput(PointerInputKind.Move, 30, 30));
        Assert.Equal(
            "pointerdown:pc:P captured true 1 mouse mousedown:pc:M",
            recording.After(new PointerInput(PointerInputKind.Down, 30, 30) { Buttons = 1 }));
        Assert.Equal(
            "gotpointercapture:pc:P pointerup:pc:P mouseup:pc:M lostpointercapture:pc:P click:pc:P",
            recording.After(new PointerInput(PointerInputKind.Up, 30, 30)));
    }

    /// <summary>
    /// While the pointer is captured its moves and its release go to the capturing element, wherever it is;
    /// once the capture is gone the next move finds the element under the pointer again.
    /// </summary>
    [Fact]
    public void ACapturedPointersEventsGoToTheCapturingElement()
    {
        using var session = Start(CaptureOnPress);
        var recording = new Recording(session);

        recording.After(new PointerInput(PointerInputKind.Move, 30, 30));
        recording.After(new PointerInput(PointerInputKind.Down, 30, 30) { Buttons = 1 });
        Assert.Equal(
            "gotpointercapture:pc:P pointermove:pc:P mousemove:pc:M",
            recording.After(new PointerInput(PointerInputKind.Move, 350, 50) { Buttons = 1 }));
        Assert.Equal(
            "pointerup:pc:P mouseup:pc:M lostpointercapture:pc:P click:pc:P",
            recording.After(new PointerInput(PointerInputKind.Up, 350, 50)));
        Assert.Equal(
            "pointerout:pc:P pointerover:other:P pointermove:other:P mousemove:other:M",
            recording.After(new PointerInput(PointerInputKind.Move, 360, 60)));
    }

    /// <summary>A capture released in a listener is lost before the next pointer event, which goes where the pointer is.</summary>
    [Fact]
    public void ACaptureReleasedByThePageEndsAtTheNextEvent()
    {
        using var session = Start(CaptureOnPress +
            "document.getElementById('pc').addEventListener('pointermove', function (e) { this.releasePointerCapture(e.pointerId); note('released ' + this.hasPointerCapture(1)); });");
        var recording = new Recording(session);

        recording.After(new PointerInput(PointerInputKind.Move, 30, 30));
        recording.After(new PointerInput(PointerInputKind.Down, 30, 30) { Buttons = 1 });
        Assert.Equal(
            "gotpointercapture:pc:P pointermove:pc:P released false mousemove:pc:M",
            recording.After(new PointerInput(PointerInputKind.Move, 350, 50) { Buttons = 1 }));
        Assert.Equal(
            "lostpointercapture:pc:P pointerout:pc:P pointerover:other:P pointermove:other:P mousemove:other:M",
            recording.After(new PointerInput(PointerInputKind.Move, 360, 60) { Buttons = 1 }));
    }

    /// <summary>
    /// An unknown pointer id throws <c>NotFoundError</c>, and with no button down a capture is neither made
    /// nor refused.
    /// </summary>
    [Fact]
    public void OnlyThePressedMouseCanBeCaptured()
    {
        using var session = Start(
            "var pc = document.getElementById('pc'), results = [];" +
            "function attempt(name, f) { try { f(); results.push(name + ' ok'); } catch (e) { results.push(name + ' ' + e.name); } }" +
            "attempt('set 7', function () { pc.setPointerCapture(7); });" +
            "attempt('release 7', function () { pc.releasePointerCapture(7); });" +
            "attempt('set 1', function () { pc.setPointerCapture(1); });" +
            "note(results.join(',') + ',has ' + pc.hasPointerCapture(1));");

        Assert.Equal("set 7 NotFoundError,release 7 NotFoundError,set 1 ok,has false", new Recording(session).Read());
    }

    /// <summary>
    /// <c>PointerEvent</c> exists with Chromium's defaults and inheritance, and the event interfaces inherit as
    /// a browser's do.
    /// </summary>
    [Fact]
    public void PointerEventHasChromiumsDefaults()
    {
        using var session = Start(
            "var p = new PointerEvent('pointerdown'), q = new PointerEvent('pointermove', { pointerId: 5, pointerType: 'pen', pressure: 0.5, width: 4, isPrimary: true, bubbles: true, buttons: 1 });" +
            "note([p.pointerId, p.width, p.height, p.pressure, p.tangentialPressure, p.tiltX, p.tiltY, p.twist, p.altitudeAngle === Math.PI / 2, p.azimuthAngle, JSON.stringify(p.pointerType), p.isPrimary, p.bubbles, p.getCoalescedEvents().length].join(','));" +
            "note([q.pointerId, q.pointerType, q.pressure, q.width, q.height, q.isPrimary, q.bubbles, q.buttons].join(','));" +
            "note([p instanceof MouseEvent, p instanceof UIEvent, p instanceof Event, new MouseEvent('x') instanceof UIEvent, new KeyboardEvent('x') instanceof Event," +
            " new WheelEvent('x') instanceof MouseEvent, new CustomEvent('x') instanceof Event, new MouseEvent('x') instanceof PointerEvent].join(','));");

        Assert.Equal(
            "0,1,1,0,0,0,0,0,true,0,\"\",false,false,0 5,pen,0.5,4,1,true,true,1 true,true,true,true,true,true,true,false",
            new Recording(session).Read());
    }
}
