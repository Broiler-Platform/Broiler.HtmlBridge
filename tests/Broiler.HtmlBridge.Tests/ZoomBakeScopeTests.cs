using System.Drawing;

using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Serialization resolves every element's computed style to bake its used <c>zoom</c> only on a page that
/// can have one other than 1, and every page still gets what those passes do besides.
/// </summary>
/// <remarks>
/// A render projection is a document of its own, so the zoom passes were two whole-document cascades for
/// each projection, which a window builds after every change it shows: a third of each pointer move over
/// html5test.com, whose only zoom is <c>zoom: 1</c>. They are now skipped where no <c>zoom</c> declaration
/// can scale. That reading of the sheets must not miss a zoom that scales, and the SVG presentation
/// attributes the first pass also writes must still be written.
/// </remarks>
public class ZoomBakeScopeTests
{
    private const string PageUrl = "https://example.test/zoom-bake-scope";

    private static readonly Dictionary<string, RectangleF> ProbeBoxes = new()
    {
        ["z"] = new RectangleF(0, 0, 200, 100),
        ["v"] = new RectangleF(0, 0, 200, 100),
    };

    private static string Render(string head, string body)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = static () => new DeclaredBoxLayoutView(ProbeBoxes),
        }));

        var html = engine.Execute(
            ["1;"],
            $"<!DOCTYPE html><html><head><style>{head}</style></head><body>{body}</body></html>",
            PageUrl);
        Assert.NotNull(html);
        return html!;
    }

    /// <summary>The opening tag with <paramref name="id"/> as the serialized document carries it.</summary>
    private static string TagOf(string html, string id)
    {
        var at = html.IndexOf($"id=\"{id}\"", StringComparison.Ordinal);
        Assert.True(at >= 0, $"no element with id=\"{id}\" in serialized output: {html}");
        return html[html.LastIndexOf('<', at)..(html.IndexOf('>', at) + 1)];
    }

    /// <summary>A sheet's fill and stroke reach an SVG shape's attributes, which the renderer reads, on a page with no zoom.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(".clearfix { zoom: 1; } .grab { cursor: zoom-in; }")]
    public void AnSvgShapesPaintReachesItsAttributesOnAPageWithoutZoom(string rest)
    {
        var tag = TagOf(
            Render("rect { fill: lime; stroke: blue; } " + rest, "<svg id=\"v\"><rect id=\"r\" width=\"10\" height=\"10\"></rect></svg>"),
            "r");

        Assert.Contains("fill=\"lime\"", tag);
        Assert.Contains("stroke=\"blue\"", tag);
    }

    /// <summary>A zoom that scales is baked however it is declared, beside others that do not.</summary>
    [Theory]
    [InlineData(".clearfix { zoom: 1; } #z { zoom: 2; width: 50px; }", "")]
    [InlineData("@media screen { #z { zoom: 2; width: 50px; } }", "")]
    [InlineData(":root { --z: 2; } #z { zoom: var(--z); width: 50px; }", "")]
    [InlineData("#z { width: 50px; }", " style=\"zoom: 2\"")]
    public void AZoomThatScalesIsStillBaked(string head, string inline)
    {
        var tag = TagOf(Render(head, $"<div id=\"z\"{inline}>z</div>"), "z");

        Assert.Contains("width: 100px", tag);
    }

    /// <summary>
    /// A zoom that scales in a sheet that is only imported is baked: what a sheet imports is read. Any
    /// import used to count as a zoom, and reCAPTCHA's demo page, which imports a font sheet, had each of
    /// its projections baked.
    /// </summary>
    [Fact]
    public void AZoomInAnImportedSheetIsBaked()
    {
        var tag = TagOf(
            Render("@import url(" + CspFixture.DataUrl("#z { zoom: 2; width: 50px; }") + ");", "<div id=\"z\">z</div>"),
            "z");

        Assert.Contains("width: 100px", tag);
    }
}
