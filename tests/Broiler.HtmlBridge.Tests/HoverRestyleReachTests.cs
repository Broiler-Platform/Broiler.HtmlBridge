using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A move of the pointer that changes what is hovered resolves again the style of every element a
/// <c>:hover</c> rule can restyle, and of no other when it can tell which those are.
/// </summary>
/// <remarks>
/// The page reads every probe's computed style as the pointer enters each element, so that each holds
/// a resolved style a missed invalidation would leave standing at the next move, and it only records
/// them: a write to the DOM would have every style resolved again. The elements whose hover state
/// changed restyle what is inside them, through inheritance as through a descendant or child
/// combinator; a sibling combinator after <c>:hover</c> also restyles their later siblings; and
/// <c>:has()</c> can restyle anything, so a sheet that uses it has every element resolved again. The
/// layout is declared (<see cref="DeclaredBoxLayoutView"/>).
/// </remarks>
public class HoverRestyleReachTests
{
    private const string PageUrl = "https://example.test/hover-reach";

    private static readonly Dictionary<string, System.Drawing.RectangleF> Boxes = new()
    {
        ["root"] = new(0, 0, 800, 600),
        ["r1"] = new(0, 0, 800, 30),
        ["c1"] = new(0, 0, 800, 30),
        ["r2"] = new(0, 30, 800, 30),
        ["c2"] = new(0, 30, 800, 30),
        ["r3"] = new(0, 60, 800, 30),
        ["c3"] = new(0, 60, 800, 30),
        ["list"] = new(0, 100, 800, 90),
        ["i1"] = new(0, 100, 800, 30),
        ["i2"] = new(0, 130, 800, 30),
        ["i3"] = new(0, 160, 800, 30),
    };

    private const string Body =
        "<table><tr id=\"r1\"><td id=\"c1\"><span id=\"s1\">one</span></td></tr>" +
        "<tr id=\"r2\"><td id=\"c2\"><span id=\"s2\">two</span></td></tr>" +
        "<tr id=\"r3\"><td id=\"c3\"><span id=\"s3\">three</span></td></tr></table>" +
        "<div id=\"list\"><p class=\"item\" id=\"i1\">a</p><p class=\"item\" id=\"i2\">b</p><p class=\"item last\" id=\"i3\">c</p></div>";

    /// <summary>
    /// Starts the page with <paramref name="style"/>. As the pointer enters an element, the page records
    /// each of <paramref name="probes"/>' colour, which resolves and so memoizes it.
    /// </summary>
    private static InteractiveSession Start(string style, params string[] probes)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = () => new DeclaredBoxLayoutView(Boxes),
        }));

        var session = engine.ExecuteInteractive(
            ["var log = [];" +
             "document.addEventListener('mouseover', function () { log.push([" +
             string.Join(",", probes.Select(probe => $"'{probe}'")) + "].map(function (id) {" +
             " return id + ' ' + getComputedStyle(document.getElementById(id)).color; }).join('|')); });"],
            [],
            "<html id=\"root\"><head><style>p, td { color: rgb(0, 0, 0); } " + style + "</style></head>" +
            "<body>" + Body + "<div id=\"out\"></div></body></html>",
            PageUrl);
        Assert.NotNull(session);
        session!.SettleLoadWindow();
        return session;
    }

    private static void Move(InteractiveSession session, double x, double y) =>
        session.DispatchPointer(new PointerInput(PointerInputKind.Move, x, y));

    /// <summary>What the page recorded at each element the pointer entered, one entry a line.</summary>
    private static string[] Recorded(InteractiveSession session)
    {
        session.RunJavaScriptUrl("javascript:void(document.getElementById('out').textContent = log.join(' / '))");
        return PageProbe.OutOf(session.SettleLoadWindow(), decode: true).Split(" / ");
    }

    /// <summary>
    /// The row the pointer leaves and the row it enters are restyled, down to what their cells inherit;
    /// the row it never touched keeps its style.
    /// </summary>
    [Fact]
    public void TheRowsTheHoverLeavesAndEntersAreRestyledDownToWhatTheyInherit()
    {
        using var session = Start("tr:hover > td { color: rgb(255, 0, 0); }", "c1", "s1", "c2", "s2", "c3", "s3");

        Move(session, 400, 10);
        Move(session, 400, 40);

        Assert.Equal(
            [
                "c1 rgb(255, 0, 0)|s1 rgb(255, 0, 0)|c2 rgb(0, 0, 0)|s2 rgb(0, 0, 0)|c3 rgb(0, 0, 0)|s3 rgb(0, 0, 0)",
                "c1 rgb(0, 0, 0)|s1 rgb(0, 0, 0)|c2 rgb(255, 0, 0)|s2 rgb(255, 0, 0)|c3 rgb(0, 0, 0)|s3 rgb(0, 0, 0)",
            ],
            Recorded(session));
    }

    /// <summary>
    /// A sibling combinator after <c>:hover</c> restyles the later siblings of the elements whose hover
    /// changed, near and far: the last item is neither of them nor in either.
    /// </summary>
    [Fact]
    public void ASiblingCombinatorAfterTheHoverRestylesLaterSiblings()
    {
        using var session = Start(
            ".item:hover + .item { color: rgb(0, 128, 0); } .item:hover ~ p { color: rgb(0, 0, 255); }",
            "i1", "i2", "i3");

        Move(session, 400, 145);
        Move(session, 400, 115);

        Assert.Equal(
            ["i1 rgb(0, 0, 0)|i2 rgb(0, 0, 0)|i3 rgb(0, 128, 0)", "i1 rgb(0, 0, 0)|i2 rgb(0, 128, 0)|i3 rgb(0, 0, 255)"],
            Recorded(session));
    }

    /// <summary>
    /// <c>:has()</c> restyles an element no other combinator reaches from those whose hover changed:
    /// here the first item, an earlier sibling of both.
    /// </summary>
    [Fact]
    public void HasRestylesAnEarlierSibling()
    {
        using var session = Start(".item:has(+ .item:hover) { color: rgb(128, 0, 128); }", "i1", "i2", "i3");

        Move(session, 400, 175);
        Move(session, 400, 145);

        Assert.Equal(
            ["i1 rgb(0, 0, 0)|i2 rgb(128, 0, 128)|i3 rgb(0, 0, 0)", "i1 rgb(128, 0, 128)|i2 rgb(0, 0, 0)|i3 rgb(0, 0, 0)"],
            Recorded(session));
    }
}
