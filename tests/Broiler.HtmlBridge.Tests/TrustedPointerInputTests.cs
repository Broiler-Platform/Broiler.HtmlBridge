using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A user's click, delivered to the page's scripts through <see cref="InteractiveSession.DispatchPointer"/>:
/// hit-tested to the element under the pointer -- into a frame -- and fired as trusted pointer, mouse
/// and click events, with a click's activation, as the frame's own script when it lands in a frame.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing a user did reached a page's scripts.</b> There was no way for a host to deliver input
/// at all, and every event a script saw was one a script had made, with <c>isTrusted</c> false. The
/// browser window handled a click itself, so a button with a click listener, a checkbox drawn by
/// script -- reCAPTCHA's, a <c>span</c> in a frame -- never answered.
/// </para>
/// <para>
/// Geometry comes from <see cref="DeclaredFrameLayoutView"/>, which places each element with an
/// <c>id</c> where the test declares it, in the page's coordinates -- a frame's elements included,
/// as the real engine lays a frame's document out in place inside its element.
/// </para>
/// </remarks>
public class TrustedPointerInputTests
{
    private const string PageUrl = "https://example.test/clicks";

    private static readonly Dictionary<string, System.Drawing.RectangleF> PageBoxes = new()
    {
        ["target"] = new(10, 10, 100, 50),
        ["inner"] = new(20, 20, 20, 20),
        ["far"] = new(10, 1500, 100, 50),
        ["frame"] = new(50, 100, 300, 200),
        ["box"] = new(60, 110, 20, 20),
        ["check"] = new(10, 80, 20, 20),
        ["label"] = new(40, 80, 60, 20),
    };

    private static InteractiveSession Start(string body, string script = "")
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = () => new DeclaredFrameLayoutView(PageBoxes),
        }));

        var session = engine.ExecuteInteractive(
            [script.Length > 0 ? script : ";"], [],
            $"<html><body style=\"margin:0\">{body}<div id=\"out\"></div></body></html>",
            PageUrl);
        Assert.NotNull(session);
        session!.SettleLoadWindow();
        return session;
    }

    private static PointerInputResult Click(InteractiveSession session, double x, double y, int clickCount = 1, double scrollY = 0)
    {
        session.DispatchPointer(new PointerInput(PointerInputKind.Down, x, y) { Buttons = 1, ClickCount = clickCount, ScrollY = scrollY });
        return session.DispatchPointer(new PointerInput(PointerInputKind.Up, x, y) { ClickCount = clickCount, ScrollY = scrollY });
    }

    private static string Out(InteractiveSession session) => PageProbe.OutOf(session.CurrentHtml(), decode: true);

    private const string Recorder =
        "var seen = [];" +
        "function record(e) { seen.push([e.type, e.isTrusted, e.clientX + ',' + e.clientY, e.button, e.detail].join(':'));" +
        " document.getElementById('out').textContent = seen.join(' '); }";

    /// <summary>
    /// A press and a release over an element fire pointerdown, mousedown, pointerup, mouseup and click
    /// at it, every one trusted, at the point in the viewport, with the click count as the mouse
    /// events' detail.
    /// </summary>
    [Fact]
    public void AClickFiresTrustedPointerMouseAndClickEventsAtTheElement()
    {
        using var session = Start("<div id=\"target\">target</div>",
            Recorder +
            "['pointerdown', 'mousedown', 'pointerup', 'mouseup', 'click'].forEach(function (type) {" +
            " document.getElementById('target').addEventListener(type, record); });");

        var result = Click(session, 30, 40);

        Assert.Equal(new PointerInputResult(true, false), result);
        Assert.Equal(
            "pointerdown:true:30,40:0:0 mousedown:true:30,40:0:1 pointerup:true:30,40:0:0 mouseup:true:30,40:0:1 click:true:30,40:0:1",
            Out(session));
    }

    /// <summary>
    /// The deepest element under the pointer is the target; the click bubbles through its ancestors
    /// to the document and the window, and each listener runs with the element it is on as
    /// <c>this</c>.
    /// </summary>
    [Fact]
    public void AClickBubblesFromTheDeepestElementToTheWindowWithThisAsTheCurrentTarget()
    {
        using var session = Start("<div id=\"target\"><span id=\"inner\">inner</span></div>",
            "var path = [];" +
            "function note() { path.push(this === window ? 'window' : this === document ? 'document' : this.id); }" +
            "document.getElementById('inner').addEventListener('click', note);" +
            "document.getElementById('target').addEventListener('click', note);" +
            "document.addEventListener('click', note);" +
            "window.addEventListener('click', note);" +
            "window.addEventListener('click', function (e) { document.getElementById('out').textContent = path.join('>') + '|' + e.target.id; });");

        Click(session, 25, 25);

        Assert.Equal("inner>target>document>window|inner", Out(session));
    }

    /// <summary>
    /// A click inside a frame reaches the frame's element, as the frame's own script: its listener
    /// sees the frame's window as <c>window</c> and as the event's <c>view</c>, and coordinates
    /// relative to the frame. The frame's document and window hear it bubble; the page's do not.
    /// </summary>
    [Fact]
    public void AClickInAFrameReachesTheFramesElementAsTheFramesScript()
    {
        using var session = Start(
            "<iframe id=\"frame\" srcdoc=\"<body><span id=box>box</span><script>" +
            "var heard = [];" +
            "document.getElementById('box').addEventListener('click', function (e) {" +
            " heard.push('box:' + e.isTrusted + ':' + (e.view === window) + ':' + e.clientX + ',' + e.clientY); });" +
            "document.addEventListener('click', function () { heard.push('frame-document'); });" +
            "window.addEventListener('click', function () { heard.push('frame-window'); pageOut.textContent = heard.join(' ') + pageHeard; });" +
            "</script></body>\"></iframe>",
            "var pageOut = document.getElementById('out'); var pageHeard = '';" +
            "document.addEventListener('click', function () { pageHeard += ' page-document'; });" +
            "window.addEventListener('click', function () { pageHeard += ' page-window'; });" +
            "document.getElementById('frame').contentWindow;");

        Click(session, 65, 115);

        Assert.Equal("box:true:true:15,15 frame-document frame-window", Out(session));
    }

    /// <summary>
    /// A page that cancels the click -- with <c>preventDefault()</c>, or an inline handler returning
    /// <c>false</c> -- is reported to the host as having done so, so the host follows no link for it.
    /// An inline handler returning false did not cancel anything.
    /// </summary>
    [Theory]
    [InlineData("<div id=\"target\" onclick=\"return false\">target</div>", "", true)]
    [InlineData("<div id=\"target\">target</div>", "document.getElementById('target').addEventListener('click', function (e) { e.preventDefault(); });", true)]
    [InlineData("<div id=\"target\" onclick=\"return true\">target</div>", "", false)]
    public void ACancelledClickIsReportedAsCancelled(string body, string script, bool cancelled)
    {
        using var session = Start(body, script);

        Assert.Equal(new PointerInputResult(true, cancelled), Click(session, 30, 40));
    }

    /// <summary>
    /// A click on a checkbox checks it before its click listeners run, then fires <c>input</c> and
    /// <c>change</c>; a click its listener cancels leaves it as it was and fires neither. The document
    /// the host renders shows the state it is in.
    /// </summary>
    [Fact]
    public void ACheckboxClickChecksItFiresInputAndChangeAndIsUndoneWhenCancelled()
    {
        using var session = Start("<input type=\"checkbox\" id=\"check\">",
            "var log = []; var check = document.getElementById('check'); var clicks = 0;" +
            "function show(entry) { log.push(entry); document.getElementById('out').textContent = log.join(' '); }" +
            "check.addEventListener('click', function (e) { clicks++; show('click:' + check.checked); if (clicks === 2) e.preventDefault(); });" +
            "check.addEventListener('input', function () { show('input'); });" +
            "check.addEventListener('change', function () { show('change'); });");

        Click(session, 15, 85);
        Assert.Equal("click:true input change", Out(session));
        Assert.Contains("checked", Attribute(session.CurrentHtml(), "check"));

        // The second click would uncheck it; its listener cancels it, so it stays checked.
        Click(session, 15, 85);
        Assert.Equal("click:true input change click:false", Out(session));
        Assert.Contains("checked", Attribute(session.CurrentHtml(), "check"));
    }

    /// <summary>A click on a label clicks the control it labels, which checks it.</summary>
    [Fact]
    public void AClickOnALabelClicksItsControl()
    {
        using var session = Start("<input type=\"checkbox\" id=\"check\"><label id=\"label\" for=\"check\">label</label>",
            "document.getElementById('check').addEventListener('change', function (e) {" +
            " document.getElementById('out').textContent = 'changed:' + e.target.checked + ':' + e.isTrusted; });");

        Click(session, 50, 85);

        Assert.Equal("changed:true:true", Out(session));
    }

    /// <summary>The second click of a double click is followed by <c>dblclick</c>, with a detail of 2.</summary>
    [Fact]
    public void ADoubleClicksSecondClickIsFollowedByDblclick()
    {
        using var session = Start("<div id=\"target\">target</div>",
            Recorder +
            "['click', 'dblclick'].forEach(function (type) { document.getElementById('target').addEventListener(type, record); });");

        Click(session, 30, 40, clickCount: 1);
        Click(session, 30, 40, clickCount: 2);

        Assert.Equal("click:true:30,40:0:1 click:true:30,40:0:2 dblclick:true:30,40:0:2", Out(session));
    }

    /// <summary>
    /// A click on a scrolled page hits the element at that point on the page, and its client
    /// coordinates are the point in the viewport.
    /// </summary>
    [Fact]
    public void AClickOnAScrolledPageHitsTheElementAtThatPointOfThePage()
    {
        using var session = Start("<div id=\"far\">far</div>",
            "document.getElementById('far').addEventListener('click', function (e) {" +
            " document.getElementById('out').textContent = e.clientY + ',' + e.pageY; });");

        Click(session, 30, 1510, scrollY: 1400);

        Assert.Equal("110,1510", Out(session));
    }

    /// <summary>
    /// Work a click's handler schedules is due for the host to step, though the load window closed
    /// long before: a spinner, a request's answer. It was past the horizon the host steps by.
    /// </summary>
    [Fact]
    public void WorkAClickSchedulesIsDueForTheHostToStep()
    {
        using var session = Start("<div id=\"target\">target</div>",
            "setTimeout(function () {}, 4000);" +
            "document.getElementById('target').addEventListener('click', function () {" +
            " setTimeout(function () { document.getElementById('out').textContent = 'later'; }, 3000); });");
        Assert.False(session.HasWorkDueInLoadWindow);

        Click(session, 30, 40);

        Assert.True(session.HasWorkDueInLoadWindow);
        while (session.HasWorkDueInLoadWindow)
            session.Step();
        Assert.Equal("later", Out(session));
    }

    private static string Attribute(string html, string id)
    {
        var at = html.IndexOf($"id=\"{id}\"", StringComparison.Ordinal);
        var start = html.LastIndexOf('<', at);
        return html[start..html.IndexOf('>', at)];
    }
}

/// <summary>
/// A layout view that places each element with an <c>id</c> where a test declares it, in the page's
/// coordinates -- a frame's elements too: it walks into a frame's document through the content-document
/// resolver, as the engine lays a frame's document out in place inside its element.
/// </summary>
internal sealed class DeclaredFrameLayoutView(IReadOnlyDictionary<string, System.Drawing.RectangleF> borderBoxesById)
    : Broiler.Layout.ILayoutView
{
    public IReadOnlyDictionary<Broiler.Dom.DomElement, Broiler.Layout.BoxGeometry> GetGeometry(
        Broiler.Dom.DomDocument document,
        System.Drawing.SizeF viewport,
        string baseUrl,
        Func<Broiler.Dom.DomElement, Broiler.Dom.DomDocument?>? contentDocumentResolver = null)
    {
        var geometry = new Dictionary<Broiler.Dom.DomElement, Broiler.Layout.BoxGeometry>(ReferenceEqualityComparer.Instance);
        Collect(document, geometry, contentDocumentResolver, depth: 0);
        return geometry;
    }

    public void Dispose()
    {
    }

    private void Collect(
        Broiler.Dom.DomNode node,
        Dictionary<Broiler.Dom.DomElement, Broiler.Layout.BoxGeometry> geometry,
        Func<Broiler.Dom.DomElement, Broiler.Dom.DomDocument?>? contentDocumentResolver,
        int depth)
    {
        foreach (var element in node.ChildElements)
        {
            if (element.GetAttributeByQualifiedName("id") is { } id && borderBoxesById.TryGetValue(id, out var box))
                geometry[element] = new Broiler.Layout.BoxGeometry(box, box, box);

            if (depth < 4 && contentDocumentResolver?.Invoke(element) is { } content)
                Collect(content, geometry, contentDocumentResolver, depth + 1);

            Collect(element, geometry, contentDocumentResolver, depth);
        }
    }
}
