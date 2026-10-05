using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The pointer moving over a page: trusted <c>pointermove</c>/<c>mousemove</c> at the element under
/// it, after the boundary events of every element it left or reached -- <c>over</c>/<c>out</c> and
/// <c>enter</c>/<c>leave</c>, pointer and mouse -- in Chromium's order, across frames too.
/// </summary>
/// <remarks>
/// <para>
/// <b>A host could only press and release.</b> Nothing told a page where the pointer was, so a menu
/// opened on <c>mouseenter</c>, a tooltip on <c>mouseover</c>, a drag on <c>mousemove</c> never
/// happened.
/// </para>
/// <para>
/// Every expected sequence here was measured in Chromium, with trusted mouse moves over the same
/// structure and listeners on every element (Chromium skips <c>enter</c>/<c>leave</c> at elements
/// nobody listens on, which no script can tell apart). The page records what its window hears in
/// the capture phase, as <c>type:target&lt;relatedTarget</c>; a frame does the same, prefixed
/// <c>F:</c>. The layout is declared (<see cref="DeclaredFrameLayoutView"/>); a point on no declared
/// box is on the root element.
/// </para>
/// </remarks>
public class PointerHoverTests
{
    private const string PageUrl = "https://example.test/hover";

    private static readonly Dictionary<string, System.Drawing.RectangleF> PageBoxes = new()
    {
        ["outer"] = new(10, 10, 200, 200),
        ["inner"] = new(50, 50, 50, 50),
        ["frame"] = new(300, 10, 300, 200),
        ["fin"] = new(310, 20, 100, 20),
        ["fdiv"] = new(310, 60, 100, 60),
    };

    private const string Types =
        "['pointerover', 'pointerenter', 'pointerout', 'pointerleave', 'pointermove', 'mouseover', 'mouseenter', 'mouseout', 'mouseleave', 'mousemove']";

    private const string Recorder =
        "var log = [], pageOut = document.getElementById('out');" +
        "function nameOf(n) { return n === window ? 'window' : n === document ? 'document' : n && n.id ? n.id : n && n.nodeName ? n.nodeName.toLowerCase() : String(n); }" +
        Types + ".forEach(function (type) { window.addEventListener(type, function (e) {" +
        " log.push(type + ':' + nameOf(e.target) + (e.relatedTarget ? '<' + nameOf(e.relatedTarget) : '')); pageOut.textContent = log.join(' '); }, true); });";

    private static InteractiveSession Start(string body, string script = "", bool record = true)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = () => new DeclaredFrameLayoutView(PageBoxes),
        }));

        var session = engine.ExecuteInteractive(
            [(record ? Recorder : "var log = [], pageOut = document.getElementById('out');") + script], [],
            $"<html><body style=\"margin:0\">{body}<div id=\"out\"></div></body></html>",
            PageUrl);
        Assert.NotNull(session);
        session!.SettleLoadWindow();
        return session;
    }

    /// <summary>Reads what the page recorded since the last read.</summary>
    private sealed class Recording(InteractiveSession session)
    {
        private int _read;

        public string Move(double x, double y, int buttons = 0) =>
            After(new PointerInput(PointerInputKind.Move, x, y) { Buttons = buttons });

        public string After(PointerInput input)
        {
            session.DispatchPointer(input);
            var all = PageProbe.OutOf(session.CurrentHtml(), decode: true).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var next = string.Join(" ", all.Skip(_read));
            _read = all.Length;
            return next;
        }
    }

    /// <summary>
    /// Into an element, into one inside it, back out to the first, off it, and straight into the
    /// inner one: each move's boundary events in Chromium's order -- out, the leaves innermost first,
    /// over, the enters outermost first, pointer then mouse -- then the move itself.
    /// </summary>
    [Fact]
    public void MovesFireTheBoundaryEventsOfEachElementInChromiumsOrder()
    {
        using var session = Start("<div id=\"outer\"><div id=\"inner\"></div></div>");
        var recording = new Recording(session);

        Assert.Equal(
            "pointerover:html pointerenter:html mouseover:html mouseenter:html pointermove:html mousemove:html",
            recording.Move(500, 500));
        Assert.Equal(
            "pointerout:html<outer pointerover:outer<html pointerenter:body<html pointerenter:outer<html " +
            "mouseout:html<outer mouseover:outer<html mouseenter:body<html mouseenter:outer<html " +
            "pointermove:outer mousemove:outer",
            recording.Move(20, 20));
        Assert.Equal(
            "pointerout:outer<inner pointerover:inner<outer pointerenter:inner<outer " +
            "mouseout:outer<inner mouseover:inner<outer mouseenter:inner<outer " +
            "pointermove:inner mousemove:inner",
            recording.Move(60, 60));
        Assert.Equal(
            "pointerout:inner<outer pointerleave:inner<outer pointerover:outer<inner " +
            "mouseout:inner<outer mouseleave:inner<outer mouseover:outer<inner " +
            "pointermove:outer mousemove:outer",
            recording.Move(20, 150));
        Assert.Equal(
            "pointerout:outer<html pointerleave:outer<html pointerleave:body<html pointerover:html<outer " +
            "mouseout:outer<html mouseleave:outer<html mouseleave:body<html mouseover:html<outer " +
            "pointermove:html mousemove:html",
            recording.Move(500, 500));
        Assert.Equal(
            "pointerout:html<inner pointerover:inner<html pointerenter:body<html pointerenter:outer<html pointerenter:inner<html " +
            "mouseout:html<inner mouseover:inner<html mouseenter:body<html mouseenter:outer<html mouseenter:inner<html " +
            "pointermove:inner mousemove:inner",
            recording.Move(60, 60));
    }

    /// <summary>
    /// A move within one element fires only the move events, which carry how far the pointer went,
    /// the held buttons, and the <c>button</c> of an event in which none changed: -1 for the pointer
    /// event, 0 for the mouse one. Every one is trusted.
    /// </summary>
    [Fact]
    public void AMoveWithinAnElementCarriesItsMovementAndButtons()
    {
        using var session = Start("<div id=\"outer\"></div>",
            "['pointermove', 'mousemove'].forEach(function (type) { document.getElementById('outer').addEventListener(type, function (e) {" +
            " log.push([type, e.isTrusted, e.button, e.buttons, e.movementX + ',' + e.movementY, e.clientX + ',' + e.clientY].join('/')); pageOut.textContent = log.join(' '); }); });");
        var recording = new Recording(session);

        recording.Move(20, 20);
        Assert.Equal(
            "pointermove:outer pointermove/true/-1/1/5,8/25,28 mousemove:outer mousemove/true/0/1/5,8/25,28",
            recording.Move(25, 28, buttons: 1));
    }

    /// <summary>
    /// Into a frame and out again: the page's boundary events at the frame element, then the frame's
    /// own, outermost first, naming nothing of the page; leaving, the frame's first, from the inside
    /// out, then the page's. The move itself goes to the frame's element, as the frame's script.
    /// </summary>
    [Fact]
    public void ThePointerCrossesIntoAFramesDocumentAndBackInChromiumsOrder()
    {
        using var session = Start(
            "<iframe id=\"frame\" srcdoc=\"<html id=fhtml><body id=fbody><input id=fin><div id=fdiv>div</div><script>" +
            Types.Replace("'", "&quot;") + ".forEach(function (type) { window.addEventListener(type, function (e) {" +
            " log.push(&quot;F:&quot; + type + &quot;:&quot; + e.target.id + (e.relatedTarget ? &quot;<&quot; + e.relatedTarget.id : &quot;&quot;) + (window === parent ? &quot;:as-page&quot; : &quot;&quot;));" +
            " pageOut.textContent = log.join(&quot; &quot;); }, true); });" +
            "</script></body></html>\"></iframe>",
            "document.getElementById('frame').contentWindow;");
        var recording = new Recording(session);

        recording.Move(500, 500);
        Assert.Equal(
            "pointerout:html<frame pointerover:frame<html pointerenter:body<html pointerenter:frame<html " +
            "mouseout:html<frame mouseover:frame<html mouseenter:body<html mouseenter:frame<html " +
            "F:pointerover:fin F:pointerenter:fhtml F:pointerenter:fbody F:pointerenter:fin " +
            "F:mouseover:fin F:mouseenter:fhtml F:mouseenter:fbody F:mouseenter:fin " +
            "F:pointermove:fin F:mousemove:fin",
            recording.Move(320, 25));
        Assert.Equal(
            "F:pointerout:fin<fdiv F:pointerleave:fin<fdiv F:pointerover:fdiv<fin F:pointerenter:fdiv<fin " +
            "F:mouseout:fin<fdiv F:mouseleave:fin<fdiv F:mouseover:fdiv<fin F:mouseenter:fdiv<fin " +
            "F:pointermove:fdiv F:mousemove:fdiv",
            recording.Move(320, 80));
        Assert.Equal(
            "F:pointerout:fdiv F:pointerleave:fdiv F:pointerleave:fbody F:pointerleave:fhtml " +
            "F:mouseout:fdiv F:mouseleave:fdiv F:mouseleave:fbody F:mouseleave:fhtml " +
            "pointerout:frame<html pointerleave:frame<html pointerleave:body<html pointerover:html<frame " +
            "mouseout:frame<html mouseleave:frame<html mouseleave:body<html mouseover:html<frame " +
            "pointermove:html mousemove:html",
            recording.Move(500, 500));
    }

    /// <summary>The pointer leaving the page leaves every element it was over, innermost first.</summary>
    [Fact]
    public void ThePointerLeavingThePageLeavesEveryElementItWasOver()
    {
        using var session = Start("<div id=\"outer\"><div id=\"inner\"></div></div>");
        var recording = new Recording(session);

        recording.Move(60, 60);
        Assert.Equal(
            "pointerout:inner pointerleave:inner pointerleave:outer pointerleave:body pointerleave:html " +
            "mouseout:inner mouseleave:inner mouseleave:outer mouseleave:body mouseleave:html",
            recording.After(new PointerInput(PointerInputKind.Leave, 0, 0)));
    }

    /// <summary>
    /// A press where the pointer was never reported is a move there first: its boundary events come
    /// before <c>pointerdown</c>, but no move event does.
    /// </summary>
    [Fact]
    public void APressWhereThePointerWasNotReportedCrossesTheBoundariesFirst()
    {
        using var session = Start("<div id=\"outer\"></div>",
            "window.addEventListener('pointerdown', function (e) { log.push('pointerdown:' + nameOf(e.target)); pageOut.textContent = log.join(' '); }, true);");
        var recording = new Recording(session);

        Assert.Equal(
            "pointerover:outer pointerenter:html pointerenter:body pointerenter:outer " +
            "mouseover:outer mouseenter:html mouseenter:body mouseenter:outer pointerdown:outer",
            recording.After(new PointerInput(PointerInputKind.Down, 20, 20) { Buttons = 1 }));
    }

    /// <summary>
    /// <c>onmouseenter</c>, <c>onmousemove</c> and <c>onmouseleave</c> in markup run, with the element
    /// as <c>this</c>. Only the older names were compiled from markup, so these never ran.
    /// </summary>
    [Fact]
    public void MarkupHandlersForTheNewEventsRun()
    {
        using var session = Start(
            "<div id=\"outer\" onmouseenter=\"log.push('enter ' + this.id)\" onmousemove=\"log.push('move ' + event.clientX)\"" +
            " onmouseleave=\"log.push('leave ' + this.id); pageOut.textContent = log.join(' ')\"></div>",
            record: false);
        var recording = new Recording(session);

        recording.Move(20, 20);
        Assert.Equal("enter outer move 20 leave outer", recording.Move(500, 500));
    }

    /// <summary>
    /// <c>RenderVersion</c> tells a host whether a move changed what the page renders: not for a move
    /// nothing listens to, nor for a serialization, on a page holding script-set styles and form
    /// state; and so for a <c>mouseenter</c> that writes an inline style, and a <c>mouseleave</c> that
    /// unchecks a checkbox, which the DOM does not record.
    /// </summary>
    [Fact]
    public void TheRenderVersionTellsAHostWhetherAMoveChangedThePage()
    {
        using var session = Start(
            "<div id=\"outer\" onmouseenter=\"this.style.color = 'red'\"" +
            " onmouseleave=\"document.getElementById('box').checked = false\"></div><input id=\"box\" type=\"checkbox\">",
            "document.getElementById('box').checked = true; document.getElementById('outer').style.background = 'white';",
            record: false);

        session.DispatchPointer(new PointerInput(PointerInputKind.Move, 500, 500));
        var settled = session.RenderVersion;
        session.CurrentHtml();
        session.DispatchPointer(new PointerInput(PointerInputKind.Move, 510, 510));
        Assert.Equal(settled, session.RenderVersion);

        session.DispatchPointer(new PointerInput(PointerInputKind.Move, 20, 20));
        var entered = session.RenderVersion;
        Assert.NotEqual(settled, entered);
        Assert.Contains("color: red", session.CurrentHtml());

        session.DispatchPointer(new PointerInput(PointerInputKind.Move, 500, 500));
        Assert.NotEqual(entered, session.RenderVersion);
    }

    /// <summary>
    /// A cancelled <c>pointerdown</c> keeps the gesture's <c>mousemove</c>s from firing while the
    /// button is held, as it keeps its <c>mousedown</c> and <c>mouseup</c> (Pointer Events §11); the
    /// next move after the release has one.
    /// </summary>
    [Fact]
    public void ACancelledPointerdownSuppressesMousemoveUntilTheRelease()
    {
        using var session = Start("<div id=\"outer\"></div>",
            "document.getElementById('outer').addEventListener('pointerdown', function (e) { e.preventDefault(); });");
        var recording = new Recording(session);

        recording.Move(20, 20);
        recording.After(new PointerInput(PointerInputKind.Down, 20, 20) { Buttons = 1 });
        Assert.Equal("pointermove:outer", recording.Move(25, 25, buttons: 1));
        recording.After(new PointerInput(PointerInputKind.Up, 25, 25));
        Assert.Equal("pointermove:outer mousemove:outer", recording.Move(30, 30));
    }
}
