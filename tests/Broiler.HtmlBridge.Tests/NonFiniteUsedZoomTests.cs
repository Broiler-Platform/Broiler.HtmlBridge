using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A used zoom this component cannot represent must not reach geometry or the serialized document.
/// </summary>
/// <remarks>
/// <para>
/// The dependency resolves a <c>zoom</c> declaration and hands the arithmetic back, with no
/// finiteness test on this side — so <c>zoom: 1e400</c> and <c>zoom: Infinity</c> both produced a
/// used zoom of <c>+∞</c>. What a page then read was not one wrong number.
/// <c>getBoundingClientRect()</c> answered <c>NaN</c> for width and height, because the rendered
/// rect divides the snapshot box by the used zoom and multiplies the size back and
/// <c>0 × ∞</c> is <c>NaN</c>; <c>offsetWidth</c>, which takes the divided half only, answered
/// <c>0</c>; and serialization, which bakes zoom-scaled lengths into the document it hands back,
/// wrote <c>style="width: Infinitypx"</c>.
/// </para>
/// <para>
/// Three walks resolve a used zoom — the geometry one and the two serialization ones — so the
/// refusal is in the one helper all three now call, on the used value rather than on the
/// declaration. That also covers the compounding: the used zoom is the specified one times the
/// parent's, so a chain of representable zooms need not have a representable product, and a guard
/// on the declaration would not have seen it.
/// </para>
/// <para>
/// <b>What refusing means here.</b> <c>1</c> — no zoom, the identity this property already has
/// when it is unset or unreadable. Not a clamp, which would scale the page by a factor nobody
/// wrote.
/// </para>
/// </remarks>
public class NonFiniteUsedZoomTests
{
    private const string PageUrl = "https://example.test/non-finite-zoom";

    /// <summary>
    /// The one box the layout view declares. Zoom is the only thing that varies between the cases
    /// below, so every difference in the answer is the zoom's.
    /// </summary>
    private static readonly Dictionary<string, System.Drawing.RectangleF> Boxes = new()
    {
        ["z"] = new System.Drawing.RectangleF(10, 20, 100, 50),
    };

    private static string Render(string page, IReadOnlyList<string> scripts)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = () => new DeclaredBoxLayoutView(Boxes),
        }));

        var html = engine.Execute(scripts, page, PageUrl);

        Assert.NotNull(html);

        return html!;
    }

    private static string Run(string page, string probe) =>
        PageProbe.OutOf(Render(page, [PageProbe.GuardedProbe(probe)]));

    private static string ZoomPage(string zoom) =>
        "<!DOCTYPE html><html><head><style>#z { zoom: " + zoom + "; }</style></head>" +
        "<body><div id=\"z\">z</div><div id=\"out\"></div></body></html>";

    private const string RectProbe =
        "(function () { var b = document.getElementById('z').getBoundingClientRect();" +
        " return [b.left, b.top, b.width, b.height].join(','); })()";

    /// <summary>
    /// A zoom that cannot be represented is not a zoom, so the element reports the box the layout
    /// view declared — the same rect an unzoomed element reports.
    /// </summary>
    [Theory]
    [InlineData("1e400")]
    [InlineData("Infinity")]
    public void AZoomThatCannotBeRepresentedIsNotAZoom(string zoom) =>
        Assert.Equal("10,20,100,50", Run(ZoomPage(zoom), RectProbe));

    /// <summary>
    /// The other half of the same arithmetic, and a different property: <c>offsetWidth</c> is the
    /// snapshot box divided by the used zoom and never multiplied back, so an infinite zoom
    /// collapsed it to <c>0</c> rather than to <c>NaN</c>. One declaration, two wrong answers of
    /// two different kinds.
    /// </summary>
    [Fact]
    public void TheUntransformedLayoutBoxSurvivesToo() =>
        Assert.Equal(
            "offsetWidth=100 offsetHeight=50 clientWidth=100",
            Run(ZoomPage("1e400"),
                "(function () { var z = document.getElementById('z');" +
                " return 'offsetWidth=' + z.offsetWidth + ' offsetHeight=' + z.offsetHeight +" +
                " ' clientWidth=' + z.clientWidth; })()"));

    /// <summary>
    /// An ancestor's zoom is the element's own used zoom multiplied in, so a refusal has to happen
    /// at every level rather than only at the element being measured.
    /// </summary>
    [Fact]
    public void AnAncestorsRefusedZoomContributesNothing()
    {
        var page =
            "<!DOCTYPE html><html><head><style>#p { zoom: 1e400; }</style></head>" +
            "<body><div id=\"p\"><div id=\"z\">z</div></div><div id=\"out\"></div></body></html>";

        Assert.Equal("10,20,100,50", Run(page, RectProbe));
    }

    /// <summary>
    /// Two zooms a double holds perfectly well whose product it does not — the case a guard on the
    /// declaration could not have caught, and the reason the test is on the used value.
    /// </summary>
    [Fact]
    public void ACompositionThatOverflowsIsNotAZoomEither()
    {
        var page =
            "<!DOCTYPE html><html><head><style>#p { zoom: 1e200; } #z { zoom: 1e200; }</style></head>" +
            "<body><div id=\"p\"><div id=\"z\">z</div></div><div id=\"out\"></div></body></html>";

        Assert.Equal("10,20,100,50", Run(page, RectProbe));
    }

    /// <summary>
    /// The third surface, and the one that is not a number a script reads but markup this component
    /// hands back: serialization bakes every zoom-scaled length into an inline style, so a refused
    /// zoom used to write <c>width: Infinitypx</c> into the document. A refused zoom now bakes
    /// nothing, which is what an unreadable one has always done.
    /// </summary>
    [Fact]
    public void TheSerializedDocumentCarriesNoScaledInfinity()
    {
        string OpeningTagOf(string zoom)
        {
            var html = Render(
                "<!DOCTYPE html><html><head><style>" +
                "#z { zoom: " + zoom + "; width: 100px; height: 50px; margin-left: 7px; }" +
                "</style></head><body><div id=\"z\">z</div><div id=\"out\"></div></body></html>",
                [PageProbe.Probe("1")]);

            var start = html.IndexOf("<div id=\"z\"", StringComparison.Ordinal);
            Assert.True(start >= 0, $"no #z div in serialized output: {html}");
            return html[start..(html.IndexOf('>', start) + 1)];
        }

        Assert.Equal(
            "overflowing[<div id=\"z\">]" +
            " symbolic[<div id=\"z\">]" +
            " unreadable[<div id=\"z\">]" +
            " representable[<div id=\"z\" style=\"width: 200px; height: 100px; margin-left: 14px\">]",
            "overflowing[" + OpeningTagOf("1e400") + "]" +
            " symbolic[" + OpeningTagOf("Infinity") + "]" +
            " unreadable[" + OpeningTagOf("garbage") + "]" +
            " representable[" + OpeningTagOf("2") + "]");
    }

    /// <summary>
    /// Every answer the refusal must not disturb. <c>NaN</c>, a percentage that overflows and a
    /// negative zoom were already answering as no zoom — the dependency refuses all three from the
    /// declaration — which is why only the two spellings above were live routes.
    /// </summary>
    [Fact]
    public void ATowerOfPreservedAnswers() =>
        Assert.Equal(
            "unset=10,20,100,50 one=10,20,100,50 two=10,20,100,50" +
            " nan=10,20,100,50 negative=10,20,100,50 overflowingPercent=10,20,100,50" +
            " garbage=10,20,100,50",
            $"unset={Run(ZoomPage("normal"), RectProbe)}" +
            $" one={Run(ZoomPage("1"), RectProbe)}" +
            $" two={Run(ZoomPage("2"), RectProbe)}" +
            $" nan={Run(ZoomPage("NaN"), RectProbe)}" +
            $" negative={Run(ZoomPage("-Infinity"), RectProbe)}" +
            $" overflowingPercent={Run(ZoomPage("1e400%"), RectProbe)}" +
            $" garbage={Run(ZoomPage("garbage"), RectProbe)}");
}
