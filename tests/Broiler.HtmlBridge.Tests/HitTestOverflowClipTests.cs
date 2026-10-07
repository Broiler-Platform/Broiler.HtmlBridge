using System.Drawing;
using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A point hits only what is shown there: the part of an element an ancestor's <c>overflow</c> clips away
/// is not hit by <c>elementsFromPoint</c> or a press, as in Chromium.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hit test took an element's whole box</b>, however much of it an ancestor clipped. reCAPTCHA's
/// image challenge shows one picture across its tiles: each tile's image is the whole picture, three or
/// four times the tile's size, shifted so that the tile's <c>overflow: hidden</c> wrapper shows its part.
/// So every tile's image covered the whole grid, and the last tile's, painted last, took every press: a
/// click on any tile selected the last one.
/// </para>
/// <para>
/// Every expected answer here is Chromium's, measured on the same arrangement; the boxes declared are the
/// ones Chromium lays it out in, untransformed (<see cref="DeclaredBoxLayoutView"/>).
/// </para>
/// </remarks>
public class HitTestOverflowClipTests
{
    private const string PageUrl = "https://example.test/clip";

    private const string Body =
        // reCAPTCHA's tiles: each wrapper shows its third of an image three tiles wide.
        "<div id=\"w0\" class=\"w\" style=\"left: 0\"><div id=\"i0\" class=\"i\" style=\"left: 0\"></div></div>" +
        "<div id=\"w1\" class=\"w\" style=\"left: 84px\"><div id=\"i1\" class=\"i\" style=\"left: -80px\"></div></div>" +
        "<div id=\"w2\" class=\"w\" style=\"left: 168px\"><div id=\"i2\" class=\"i\" style=\"left: -160px\"></div></div>" +
        // An absolutely positioned box escapes the clip of a static ancestor below its containing block.
        "<div id=\"clipS\" style=\"position: absolute; left: 0; top: 100px; width: 100px; height: 50px\">" +
        "<div id=\"mid\" style=\"overflow: hidden; width: 100px; height: 50px\">" +
        "<div id=\"relIn\" style=\"position: relative; width: 300px; height: 20px\"></div>" +
        "<div id=\"absEsc\" style=\"position: absolute; left: 0; top: 30px; width: 300px; height: 50px\"></div>" +
        "</div></div>" +
        // A fixed box escapes a positioned ancestor's clip; an absolutely positioned one does not.
        "<div id=\"clipF\" style=\"position: absolute; left: 0; top: 200px; width: 100px; height: 50px; overflow: hidden\">" +
        "<div id=\"fixEsc\" style=\"position: fixed; left: 0; top: 200px; width: 300px; height: 20px\"></div>" +
        "<div id=\"absIn\" style=\"position: absolute; left: 0; top: 25px; width: 300px; height: 20px\"></div>" +
        "</div>" +
        // A selected tile: the wrapper is scaled down, and clips to its scaled box.
        "<div id=\"sel\" style=\"position: absolute; left: 0; top: 300px; width: 80px; height: 80px\">" +
        "<div id=\"sw\" style=\"overflow: hidden; position: relative; transform: scale(.8); width: 80px; height: 80px\">" +
        "<div id=\"si\" style=\"position: relative; width: 240px; height: 80px\"></div>" +
        "</div></div>" +
        // overflow-x: clip clips across only; down, the overflow is visible.
        "<div id=\"cx\" style=\"position: absolute; left: 0; top: 400px; width: 100px; height: 20px; overflow-x: clip\">" +
        "<div id=\"cxi\" style=\"width: 300px; height: 60px\"></div>" +
        "</div>" +
        // The body's overflow went to the viewport: it does not clip what is below the body.
        "<div id=\"below\" style=\"position: absolute; left: 0; top: 520px; width: 100px; height: 20px\"></div>";

    private static readonly Dictionary<string, RectangleF> Boxes = new()
    {
        ["root"] = new(0, 0, 800, 500),
        ["body"] = new(0, 0, 800, 500),
        ["w0"] = new(0, 0, 80, 80),
        ["i0"] = new(0, 0, 240, 80),
        ["w1"] = new(84, 0, 80, 80),
        ["i1"] = new(4, 0, 240, 80),
        ["w2"] = new(168, 0, 80, 80),
        ["i2"] = new(8, 0, 240, 80),
        ["clipS"] = new(0, 100, 100, 50),
        ["mid"] = new(0, 100, 100, 50),
        ["relIn"] = new(0, 100, 300, 20),
        ["absEsc"] = new(0, 130, 300, 50),
        ["clipF"] = new(0, 200, 100, 50),
        ["fixEsc"] = new(0, 200, 300, 20),
        ["absIn"] = new(0, 225, 300, 20),
        ["sel"] = new(0, 300, 80, 80),
        ["sw"] = new(0, 300, 80, 80),
        ["si"] = new(0, 300, 240, 80),
        ["cx"] = new(0, 400, 100, 20),
        ["cxi"] = new(0, 400, 300, 60),
        ["below"] = new(0, 520, 100, 20),
    };

    private static InteractiveSession Start(string script)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = () => new DeclaredBoxLayoutView(Boxes),
        }));
        var session = engine.ExecuteInteractive(
            ["var out = document.getElementById('out'), log = [];" +
             "function ids(x, y) { return document.elementsFromPoint(x, y).map(function (e) { return e.id || e.tagName; }).join(','); }" +
             "function note(entry) { log.push(entry); out.textContent = log.join('|'); }" + script],
            [],
            "<html id=\"root\"><head><style>" +
            "body { margin: 0; height: 500px; overflow: hidden }" +
            ".w { position: absolute; top: 0; width: 80px; height: 80px; overflow: hidden; transform: scale(1) }" +
            ".i { position: relative; width: 240px; height: 80px }" +
            "#out { display: none }" +
            $"</style></head><body id=\"body\">{Body}<div id=\"out\"></div></body></html>",
            PageUrl);
        Assert.NotNull(session);
        return session!;
    }

    /// <summary>Each tile's centre hits that tile's image, not the last tile's, whose box covers them all.</summary>
    [Fact]
    public void ATileHitsItsOwnPartOfThePicture()
    {
        using var session = Start("[[40, 40], [124, 40], [208, 40]].forEach(function (p) { note(ids(p[0], p[1])); });");

        Assert.Equal("i0,w0,body,root|i1,w1,body,root|i2,w2,body,root", PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }

    /// <summary>A press on a tile clicks that tile: reCAPTCHA's challenge selected the last tile whichever was clicked.</summary>
    [Fact]
    public void APressOnATileClicksThatTile()
    {
        using var session = Start(
            "['w0', 'w1', 'w2'].forEach(function (id) {" +
            " document.getElementById(id).addEventListener('click', function (e) { note('click ' + id + ' on ' + e.target.id); }); });");
        session.SettleLoadWindow();

        foreach (var x in new[] { 40, 124 })
        {
            session.DispatchPointer(new PointerInput(PointerInputKind.Down, x, 40) { Buttons = 1 });
            session.DispatchPointer(new PointerInput(PointerInputKind.Up, x, 40));
        }

        Assert.Equal("click w0 on i0|click w1 on i1", PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }

    /// <summary>
    /// An ancestor clips the boxes whose containing block it is or is inside: an absolutely positioned box
    /// escapes the clip of a static ancestor below its containing block, and a fixed one the clip of a
    /// positioned ancestor, which an absolutely positioned one does not escape.
    /// </summary>
    [Fact]
    public void ABoxEscapesTheClipsOfAncestorsOutsideItsContainingBlock()
    {
        using var session = Start(
            "[[200, 110], [50, 110], [200, 140], [200, 210], [200, 235], [50, 235]].forEach(function (p) { note(ids(p[0], p[1])); });");

        Assert.Equal(
            "body,root|relIn,mid,clipS,body,root|absEsc,body,root|" +
            "fixEsc,body,root|body,root|absIn,clipF,body,root",
            PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }

    /// <summary>A scaled wrapper clips to its scaled box: what shows of a selected tile is all of it that is hit.</summary>
    [Fact]
    public void ATransformedAncestorClipsToItsTransformedBox()
    {
        using var session = Start("[[4, 340], [40, 340], [100, 340]].forEach(function (p) { note(ids(p[0], p[1])); });");

        Assert.Equal("sel,body,root|si,sw,sel,body,root|body,root", PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }

    /// <summary>
    /// <c>overflow-x: clip</c> clips across and not down; and the body's <c>overflow: hidden</c>, which went
    /// to the viewport, clips nothing below the body.
    /// </summary>
    [Fact]
    public void OneAxisClipsAndTheBodysOverflowGoesToTheViewport()
    {
        using var session = Start("[[200, 410], [50, 450], [10, 530]].forEach(function (p) { note(ids(p[0], p[1])); });");

        Assert.Equal("body,root|cxi,body,root|below,root", PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }
}
