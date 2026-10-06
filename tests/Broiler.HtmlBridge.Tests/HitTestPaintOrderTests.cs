using System.Drawing;
using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A point hits the element painted on top of it: <c>elementsFromPoint</c> and a press go by CSS 2.1
/// Appendix E's painting order and HTML's top layer, as Chromium's do.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hit test took the last element in tree order</b> that covered the point. So a positioned button
/// with an in-flow block after it lost its press to the block, a <c>z-index: -1</c> box beat its parent's
/// in-flow content, and nothing behind an open modal dialog was out of reach.
/// </para>
/// <para>
/// Every expected order here is Chromium's, measured on the same arrangement. The layout is declared (<see
/// cref="DeclaredBoxLayoutView"/>); the painting order is the
/// bridge's own.
/// </para>
/// </remarks>
public class HitTestPaintOrderTests
{
    private const string PageUrl = "https://example.test/hit";

    private static InteractiveSession Start(string body, IReadOnlyDictionary<string, RectangleF> boxes, string script = "")
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = () => new DeclaredBoxLayoutView(boxes),
        }));
        var session = engine.ExecuteInteractive(
            ["var out = document.getElementById('out'), log = [];" +
             "function ids(x, y) { return document.elementsFromPoint(x, y).map(function (e) { return e.id || e.tagName; }).join(','); }" +
             "function note(entry) { log.push(entry); out.textContent = log.join('|'); }" + script],
            [], $"<html id=\"root\"><body id=\"body\">{body}<div id=\"out\"></div></body></html>", PageUrl);
        Assert.NotNull(session);
        return session!;
    }

    /// <summary>Positioned boxes, z-index, floats, inline-blocks and stacking contexts, each as Chromium stacks them.</summary>
    [Fact]
    public void ElementsAreHitInPaintingOrder()
    {
        using var session = Start(
            "<div id=\"abs\" style=\"position: absolute\"></div><div id=\"flow\"></div>" +
            "<div id=\"zwrap\" style=\"position: relative\"><div id=\"zneg\" style=\"position: absolute; z-index: -1\"></div><div id=\"zflow\"></div></div>" +
            "<div id=\"zbox\" style=\"position: relative\"><div id=\"hi\" style=\"position: absolute; z-index: 2\"></div><div id=\"lo\" style=\"position: absolute; z-index: 1\"></div></div>" +
            "<div id=\"fbox\"><div id=\"fl\" style=\"float: left\"></div><div id=\"after\"></div></div>" +
            "<div id=\"ibox\"><span id=\"ib\" style=\"display: inline-block\"></span><div id=\"lateabs\" style=\"position: absolute\"></div></div>" +
            "<div id=\"obox\" style=\"position: relative\"><div id=\"op\" style=\"opacity: 0.5\"></div><div id=\"pos0\" style=\"position: absolute\"></div></div>",
            new Dictionary<string, RectangleF>
            {
                ["root"] = new(0, 0, 1024, 768),
                ["body"] = new(0, 0, 1024, 600),
                ["abs"] = new(0, 0, 100, 50),
                ["flow"] = new(0, 0, 1024, 100),
                ["zwrap"] = new(0, 100, 1024, 100),
                ["zneg"] = new(0, 100, 100, 100),
                ["zflow"] = new(0, 100, 1024, 100),
                ["zbox"] = new(0, 200, 1024, 100),
                ["hi"] = new(0, 200, 100, 100),
                ["lo"] = new(0, 200, 100, 100),
                ["fbox"] = new(0, 300, 1024, 100),
                ["fl"] = new(0, 300, 100, 100),
                ["after"] = new(0, 300, 1024, 100),
                ["ibox"] = new(0, 400, 1024, 100),
                ["ib"] = new(0, 400, 100, 50),
                ["lateabs"] = new(0, 400, 100, 100),
                ["obox"] = new(0, 500, 1024, 100),
                ["op"] = new(0, 500, 100, 100),
                ["pos0"] = new(0, 500, 100, 100),
            },
            "[10, 110, 210, 310, 410, 510].forEach(function (y) { note(ids(10, y)); });");

        Assert.Equal(
            "abs,flow,body,root|" +
            "zflow,zwrap,body,zneg,root|" +
            "hi,lo,zbox,body,root|" +
            "fl,after,fbox,body,root|" +
            "lateabs,ib,ibox,body,root|" +
            "pos0,op,obox,body,root",
            PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }

    /// <summary>
    /// An open modal dialog is on top, and the rest of the document is inert under its backdrop: inside the
    /// dialog a point hits what of it is there, outside it the dialog itself; a press outside it is the
    /// dialog's, never the covered button's.
    /// </summary>
    [Fact]
    public void AModalDialogCoversTheDocument()
    {
        using var session = Start(
            "<button id=\"behind\" style=\"position: absolute; z-index: 10\">behind</button>" +
            "<dialog id=\"modal\"><button id=\"mb\">m</button></dialog>",
            new Dictionary<string, RectangleF>
            {
                ["root"] = new(0, 0, 1024, 768),
                ["body"] = new(0, 0, 1024, 600),
                ["behind"] = new(0, 0, 300, 300),
                ["modal"] = new(100, 100, 200, 100),
                ["mb"] = new(110, 110, 50, 30),
            },
            "document.getElementById('modal').showModal();" +
            "note(ids(120, 120)); note(ids(20, 20));" +
            "document.addEventListener('click', function (e) { note('click ' + (e.target.id || e.target.tagName)); });");
        session.SettleLoadWindow();

        session.DispatchPointer(new PointerInput(PointerInputKind.Down, 20, 20) { Buttons = 1 });
        session.DispatchPointer(new PointerInput(PointerInputKind.Up, 20, 20));

        Assert.Equal("mb,modal,root|modal,root|click modal", PageProbe.OutOf(session.SettleLoadWindow(), decode: true));
    }
}
