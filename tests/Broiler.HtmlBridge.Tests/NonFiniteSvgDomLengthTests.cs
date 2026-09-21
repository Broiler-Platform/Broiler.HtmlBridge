using System.Drawing;

using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The SVG DOM's own readers of a geometry attribute — <c>SVGAnimatedLength</c>,
/// <c>SVGAnimatedRect</c> and the <c>SVGTextContentElement</c> metrics — answer a number this
/// component can represent, or the zero they already answer for an attribute they cannot read.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="NonFiniteSvgGeometryTests"/> closed the <em>geometry</em> side of these attributes:
/// <c>ResolveSvgLength</c> reads them through <c>TryParseFiniteScalar</c>, so
/// <c>&lt;rect width="1e400"&gt;</c> has no client rect and takes no hit test. The IDL stubs in
/// <c>Features/SvgElementBinding.cs</c> are the <em>other</em> reader of the same markup, and they
/// had no finiteness test at all — so one attribute answered <c>0,0,0,0</c> to
/// <c>getBoundingClientRect()</c> and <c>Infinity</c> to <c>rect.width.baseVal.value</c> on the
/// same page. The two disagreeing is what makes this a route of its own rather than a duplicate.
/// </para>
/// <para>
/// Zero is not invented here. Every one of these reads already answers zero for an attribute it
/// cannot parse — absent, <c>"junk"</c>, a percentage — because a failed
/// <c>double.TryParse</c> leaves its result at zero; the preserved cases below pin that, so
/// "unrepresentable" and "unreadable" are demonstrably one answer rather than two that agree.
/// </para>
/// </remarks>
public class NonFiniteSvgDomLengthTests
{
    private const string PageUrl = "https://example.test/non-finite-svg-dom";

    /// <summary>The <c>&lt;svg&gt;</c> gets a box; no shape does, which is the point.</summary>
    private static readonly Dictionary<string, RectangleF> ProbeBoxes = new()
    {
        ["v"] = new RectangleF(0, 0, 200, 100),
    };

    private static string Answer(string svgChildren, string expression, string svgAttributes = "")
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = static () => new DeclaredBoxLayoutView(ProbeBoxes),
        }));

        var html = engine.Execute(
            [PageProbe.GuardedProbe(expression)],
            "<!DOCTYPE html><html><head><title>t</title></head><body>" +
            $"<svg id=\"v\"{svgAttributes}>{svgChildren}</svg>" +
            "<div id=\"out\"></div></body></html>",
            PageUrl);

        Assert.NotNull(html);

        return PageProbe.OutOf(html!);
    }

    private const string Rect = "<rect id=\"r\" x=\"0\" y=\"0\" width=\"10\" height=\"10\"></rect>";

    /// <summary>
    /// A dimensional attribute this component cannot represent is not a length on the IDL either.
    /// Before this, <c>NumberStyles.Any</c> handed the infinity straight to <c>baseVal.value</c>.
    /// </summary>
    [Theory]
    [InlineData("<rect id=\"r\" width=\"1e400\" height=\"10\"></rect>", "width")]
    [InlineData("<rect id=\"r\" width=\"Infinity\" height=\"10\"></rect>", "width")]
    [InlineData("<rect id=\"r\" width=\"-Infinity\" height=\"10\"></rect>", "width")]
    [InlineData("<rect id=\"r\" width=\"NaN\" height=\"10\"></rect>", "width")]
    [InlineData("<rect id=\"r\" width=\"10\" height=\"1e400\"></rect>", "height")]
    [InlineData("<rect id=\"r\" x=\"1e400\" width=\"10\" height=\"10\"></rect>", "x")]
    [InlineData("<rect id=\"r\" y=\"Infinity\" width=\"10\" height=\"10\"></rect>", "y")]
    [InlineData("<circle id=\"r\" cx=\"5\" cy=\"5\" r=\"1e400\"></circle>", "r")]
    [InlineData("<ellipse id=\"r\" cx=\"5\" cy=\"5\" rx=\"1e400\" ry=\"5\"></ellipse>", "rx")]
    public void ADimensionThatCannotBeRepresentedIsNotALength(string svgChildren, string property) =>
        Assert.Equal("0", Answer(svgChildren, $"document.getElementById('r').{property}.baseVal.value"));

    /// <summary>
    /// The same through a run of digits: no exponent and no symbol anywhere in the markup, and
    /// still past what a double holds.
    /// </summary>
    [Fact]
    public void ARunOfDigitsTooLongForADoubleIsNotALengthEither() =>
        Assert.Equal(
            "0",
            Answer(
                $"<rect id=\"r\" width=\"{new string('1', 401)}\" height=\"10\"></rect>",
                "document.getElementById('r').width.baseVal.value"));

    /// <summary>
    /// <c>animVal</c> is minted from the same number as <c>baseVal</c>, so it is refused with it.
    /// </summary>
    [Fact]
    public void TheAnimatedValueIsRefusedWithTheBaseValue() =>
        Assert.Equal(
            "0,0",
            Answer(
                "<rect id=\"r\" width=\"1e400\" height=\"10\"></rect>",
                "(function () { var w = document.getElementById('r').width;" +
                " return [w.baseVal.value, w.animVal.value].join(','); })()"));

    /// <summary>
    /// A <c>viewBox</c> component this component cannot represent is not a viewBox component. Its
    /// four numbers are read individually, so each spelling is pinned on the number it lands in.
    /// </summary>
    [Theory]
    [InlineData("0 0 1e400 10", "0,0,0,10")]
    [InlineData("0 0 Infinity 10", "0,0,0,10")]
    [InlineData("NaN 0 20 10", "0,0,20,10")]
    [InlineData("0 1e400 20 10", "0,0,20,10")]
    [InlineData("0 0 20 -Infinity", "0,0,20,0")]
    public void AViewBoxComponentThatCannotBeRepresentedIsZero(string viewBox, string expected) =>
        Assert.Equal(
            expected,
            Answer(
                Rect,
                "(function () { var b = document.getElementById('v').viewBox.baseVal;" +
                " return [b.x, b.y, b.width, b.height].join(','); })()",
                $" viewBox=\"{viewBox}\""));

    /// <summary>
    /// The text metrics multiply the <c>font-size</c> attribute by a character count, so a font
    /// size that cannot be represented used to answer <c>Infinity</c> for a text length and a
    /// character position of <c>{x: NaN, y: Infinity}</c> — one attribute, three stubs.
    /// </summary>
    [Theory]
    [InlineData("1e400")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("NaN")]
    public void AFontSizeThatCannotBeRepresentedIsNotAFontSize(string fontSize) =>
        Assert.Equal(
            "0|0,0|0",
            Answer(
                $"<text id=\"tx\" font-size=\"{fontSize}\">hello</text>",
                "(function () { var t = document.getElementById('tx');" +
                " var p = t.getStartPositionOfChar(0);" +
                " return [t.getComputedTextLength(), p.x + ',' + p.y," +
                " t.getSubStringLength(0, 2)].join('|'); })()"));

    /// <summary>
    /// What the refusal must not disturb, in one answer: an attribute this component reads is
    /// unchanged, the exponent form that <em>is</em> representable still reads as its value, and
    /// every spelling the reads already refused still answers exactly what it answered before —
    /// which is the zero a refused value now takes.
    /// </summary>
    [Fact]
    public void ATowerOfPreservedAnswers() =>
        Assert.Equal(
            string.Join('|',
                "100",   // a length this component reads
                "100",   // the exponent form that IS representable
                "0",     // a percentage: not a number, and never was
                "0",     // unreadable
                "0",     // absent
                "-5",    // negative, and representable
                "0,0,20,10", // a viewBox this component reads
                "48",    // an absent font-size is the 16px default: 5 chars x 16 x 0.6
                "0"),    // an unreadable font-size, which is the path a refused one takes
            Answer(
                "<rect id=\"a\" width=\"100\" height=\"10\"></rect>" +
                "<rect id=\"b\" width=\"1e2\" height=\"10\"></rect>" +
                "<rect id=\"c\" width=\"50%\" height=\"10\"></rect>" +
                "<rect id=\"d\" width=\"junk\" height=\"10\"></rect>" +
                "<rect id=\"e\" height=\"10\"></rect>" +
                "<rect id=\"f\" width=\"-5\" height=\"10\"></rect>" +
                "<text id=\"g\">hello</text>" +
                "<text id=\"h\" font-size=\"junk\">hello</text>",
                "[document.getElementById('a').width.baseVal.value," +
                " document.getElementById('b').width.baseVal.value," +
                " document.getElementById('c').width.baseVal.value," +
                " document.getElementById('d').width.baseVal.value," +
                " document.getElementById('e').width.baseVal.value," +
                " document.getElementById('f').width.baseVal.value," +
                " (function () { var v = document.getElementById('v').viewBox.baseVal;" +
                " return [v.x, v.y, v.width, v.height].join(','); })()," +
                " document.getElementById('g').getComputedTextLength()," +
                " document.getElementById('h').getComputedTextLength()].join('|')",
                " viewBox=\"0 0 20 10\""));
}
