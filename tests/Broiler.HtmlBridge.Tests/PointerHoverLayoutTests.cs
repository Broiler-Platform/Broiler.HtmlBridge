using Broiler.Dom;
using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;
using Broiler.Layout;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A pointer moving over a page lays the page out again only when what it changed can move a box: a hover
/// the page's sheets only paint does not, and neither does the serialization a host makes after each change.
/// </summary>
/// <remarks>
/// Over html5test.com, whose rows are styled <c>tr:hover</c> with a background colour, each change of row
/// cost the window seconds. Every serialization built a render projection, which moved the epoch the
/// retained layout is keyed on, so the move after it laid the whole page out again. The layout is declared
/// (<see cref="DeclaredBoxLayoutView"/>) and counted. The epoch is the process's, so these run apart from
/// every other test (<see cref="RetainedLayoutCollection"/>): another page's state change lays this one
/// out again too.
/// </remarks>
[Collection(nameof(RetainedLayoutCollection))]
public class PointerHoverLayoutTests
{
    private const string PageUrl = "https://example.test/rows";

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

    private static InteractiveSession Start(string hoverRule, LayoutCount count)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = () => new CountingLayoutView(
                new DeclaredBoxLayoutView(new Dictionary<string, System.Drawing.RectangleF>
                {
                    ["root"] = new(0, 0, 800, 600),
                    ["first"] = new(0, 0, 800, 30),
                    ["second"] = new(0, 30, 800, 30),
                }),
                count),
        }));

        // The listeners only record: a page that changes its DOM on hover is laid out again for that.
        var session = engine.ExecuteInteractive(
            ["var log = [];" +
             "['first', 'second'].forEach(function (id) {" +
             " document.getElementById(id).addEventListener('mouseover', function () {" +
             "  log.push(id + ' ' + getComputedStyle(document.getElementById(id)).backgroundColor); }); });"],
            [],
            "<html id=\"root\"><head><style>td { background-color: white; } " + hoverRule + "</style></head>" +
            "<body><table><tr><td id=\"first\">one</td></tr><tr><td id=\"second\">two</td></tr></table>" +
            "<div id=\"out\"></div></body></html>",
            PageUrl);
        Assert.NotNull(session);
        session!.SettleLoadWindow();
        return session;
    }

    private static void Move(InteractiveSession session, double x, double y) =>
        session.DispatchPointer(new PointerInput(PointerInputKind.Move, x, y));

    /// <summary>What the page recorded, once the layouts have been counted.</summary>
    private static string Recorded(InteractiveSession session)
    {
        session.RunJavaScriptUrl("javascript:void(document.getElementById('out').textContent = log.join('|'))");
        return PageProbe.OutOf(session.SettleLoadWindow(), decode: true);
    }

    /// <summary>
    /// A hover the page's sheets style with a colour moves no box: the page is laid out for the first hit
    /// test and not again, through two changes of row, the host's serialization after each, and a move
    /// within the row -- and the page still sees each row it enters hovered.
    /// </summary>
    [Fact]
    public void AHoverTheSheetsOnlyPaintLaysNothingOut()
    {
        var count = new LayoutCount();
        using var session = Start("tr:hover > td { background-color: rgb(238, 238, 238); }", count);

        Move(session, 10, 10);
        session.CurrentHtml();
        var layouts = count.Layouts;
        Assert.True(layouts > 0);

        Move(session, 20, 15);
        Move(session, 10, 40);
        session.CurrentHtml();
        Move(session, 20, 45);

        Assert.Equal(layouts, count.Layouts);
        Assert.Equal("first rgb(238, 238, 238)|second rgb(238, 238, 238)", Recorded(session));
    }

    /// <summary>
    /// A hover the page's sheets style with a padding can move every box after it, so the move after the
    /// change of row lays the page out again -- with no serialization in between to do it.
    /// </summary>
    [Fact]
    public void AHoverThatCanMoveABoxLaysThePageOutAgain()
    {
        var count = new LayoutCount();
        using var session = Start("tr:hover > td { padding: 10px; }", count);

        Move(session, 10, 10);
        Move(session, 20, 15);
        var layouts = count.Layouts;

        Move(session, 10, 40);
        Move(session, 20, 45);

        Assert.Equal(layouts + 1, count.Layouts);
    }
}

/// <summary>
/// Tests that count how often a page is laid out, run with no other test beside them. A retained layout is
/// keyed on an epoch every page in the process moves, so a test running at the same time lays theirs out
/// again.
/// </summary>
[CollectionDefinition(nameof(RetainedLayoutCollection), DisableParallelization = true)]
public sealed class RetainedLayoutCollection;
