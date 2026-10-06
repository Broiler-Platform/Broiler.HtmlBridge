using Broiler.Dom;
using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;
using Broiler.Layout;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The user's scroll of the host's view reaches the page without laying the page out, and is a step of the
/// page only when something listens for it.
/// </summary>
/// <remarks>
/// Each wheel notch over html5test.com cost the window seconds on the thread it draws on: the bridge clamped
/// the host's position against the page's scroll extents, a layout of the whole document, and the frame that
/// fired <c>scroll</c> to nobody was a step that serialized the whole document. The layout is declared
/// (<see cref="DeclaredBoxLayoutView"/>) and counted.
/// </remarks>
public class HostScrollTests
{
    private const string PageUrl = "https://example.test/scroll";

    private sealed class LayoutCount
    {
        public int Layouts { get; set; }
    }

    private sealed class CountingLayoutView(ILayoutView inner, LayoutCount count) : ILayoutView
    {
        public IReadOnlyDictionary<DomElement, BoxGeometry> GetGeometry(
            DomDocument document,
            System.Drawing.SizeF viewport,
            string baseUrl,
            Func<DomElement, DomDocument?>? contentDocumentResolver = null)
        {
            count.Layouts++;
            return inner.GetGeometry(document, viewport, baseUrl, contentDocumentResolver);
        }

        public void Dispose() => inner.Dispose();
    }

    private static InteractiveSession Start(string script, LayoutCount count, string body = "<body>")
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = () => new CountingLayoutView(
                new DeclaredBoxLayoutView(new Dictionary<string, System.Drawing.RectangleF>
                {
                    ["root"] = new(0, 0, 1024, 5000),
                }),
                count),
        }));

        var session = engine.ExecuteInteractive(
            ["var out = document.getElementById('out'), log = [];" +
             "function show() { out.textContent = log.join('|'); }" +
             "function at(label) { log.push(label + ' ' + Math.round(scrollY)); }" + script + ";show();"],
            [], "<html id=\"root\">" + body + "<div id=\"out\"></div></body></html>", PageUrl);
        Assert.NotNull(session);
        return session!;
    }

    /// <summary>
    /// The page's viewport goes where the host shows it, with no layout: not after a serialization either,
    /// which every step of the page ends in and which moves what a retained layout is keyed on.
    /// </summary>
    [Fact]
    public void TheHostsScrollLaysNothingOut()
    {
        var count = new LayoutCount();
        using var session = Start(
            "addEventListener('scroll', function () { at('scroll'); show(); });" +
            "at('height ' + document.documentElement.scrollHeight);",
            count);
        session.SettleLoadWindow();
        Assert.True(count.Layouts > 0);
        session.CurrentHtml();
        int layouts = count.Layouts;

        session.ScrollViewportTo(0, 1200);

        Assert.Equal(layouts, count.Layouts);
        Assert.Equal(1200, session.ViewportScroll.Y);
        Assert.EndsWith("scroll 1200", PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }

    /// <summary>
    /// A page that listens for neither <c>scroll</c> nor <c>scrollend</c> is not stepped for the user's
    /// scroll, nor are the timers past its load window run for it -- and it still reads where it is
    /// scrolled to at its next task.
    /// </summary>
    [Fact]
    public void AScrollNothingListensForIsNoStep()
    {
        var count = new LayoutCount();
        using var session = Start(
            "document.addEventListener('click', function () { at('click'); show(); });" +
            "setTimeout(function () {}, 4000); setTimeout(function () { at('later'); show(); }, 7000);",
            count);
        session.SettleLoadWindow();
        Assert.False(session.HasWorkDueInLoadWindow);

        session.ScrollViewportTo(0, 700);

        Assert.False(session.HasWorkDueInLoadWindow);
        Assert.Equal(700, session.ViewportScroll.Y);
        session.DispatchPointer(new PointerInput(PointerInputKind.Down, 20, 20) { Buttons = 1 });
        session.DispatchPointer(new PointerInput(PointerInputKind.Up, 20, 20));
        Assert.Equal("click 700", PageProbe.OutOf(session.CurrentHtml(), decode: true));
    }

    /// <summary>
    /// Every way a page hears a scroll of its viewport still makes the user's scroll a task, which
    /// delivers it in the next frame.
    /// </summary>
    [Theory]
    [InlineData("addEventListener('scroll', hear);", "<body>")]
    [InlineData("addEventListener('scrollend', hear);", "<body>")]
    [InlineData("onscroll = hear;", "<body>")]
    [InlineData("document.addEventListener('scroll', hear, true);", "<body>")]
    [InlineData("document.documentElement.addEventListener('scroll', hear);", "<body>")]
    public void AScrollThePageListensForIsHeardInTheNextFrame(string listen, string body)
    {
        var count = new LayoutCount();
        using var session = Start("function hear() { at('heard'); show(); }" + listen, count, body);
        session.SettleLoadWindow();

        session.ScrollViewportTo(0, 700);

        Assert.True(session.HasWorkDueInLoadWindow);
        Assert.Contains("heard 700", PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }
}
