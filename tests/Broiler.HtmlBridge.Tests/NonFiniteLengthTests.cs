namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A length this component cannot represent must not reach geometry, whichever unit it was
/// written in.
/// </summary>
/// <remarks>
/// <para>
/// Two things produce one: .NET's <c>NumberStyles.Float</c> accepts the symbolic forms
/// <c>NaN</c> and <c>Infinity</c>, and an exponent can overflow a double on its own, so
/// <c>1e400px</c> is a perfectly ordinary-looking declaration that parses to <c>+∞</c>.
/// <c>CssValueParser.TryParseNumeric</c> answers that way deliberately — CSS Values 4 §11.1
/// clamps an out-of-range number rather than invalidating the declaration — so the refusal has
/// to happen on this side, where the geometry that multiplies and adds these has no clamp.
/// </para>
/// <para>
/// An earlier round closed the <c>TryParsePx</c> door and left this one open, which is why these
/// read through <c>clientTop</c> rather than through the parser: the question is not what a
/// helper answers, it is what a page can observe. <c>border-top-width: 1e400px</c> answered
/// <c>element.clientTop === Infinity</c>, and the font-relative units still admitted the
/// symbolic spelling the earlier round was supposed to have removed.
/// </para>
/// </remarks>
public class NonFiniteLengthTests
{
    private const string PageUrl = "https://example.test/non-finite-length";

    private static string PageWith(string borderWidth) =>
        "<html><head><style>" +
        "#host { font-size: 16px; border-top-style: solid; border-top-width: " + borderWidth + "; }" +
        "</style></head><body><div id=\"host\">h</div><div id=\"out\"></div></body></html>";

    private static string ClientTopFor(string borderWidth) =>
        PageProbe.OutOf(PageProbe.Render(
            [PageProbe.GuardedProbe("document.getElementById('host').clientTop")],
            PageWith(borderWidth),
            PageUrl));

    /// <summary>
    /// An overflowing or symbolic border width is not a width, so the border contributes nothing
    /// and <c>clientTop</c> is the ordinary zero. The value that matters is "not Infinity"; zero
    /// is what "no usable border width" means here.
    /// </summary>
    [Theory]
    // an exponent that overflows, in every unit the evaluator resolves itself
    [InlineData("1e400px")]
    [InlineData("-1e400px")]
    [InlineData("1e400em")]
    [InlineData("1e400rem")]
    [InlineData("1e400vw")]
    [InlineData("1e400vh")]
    // the symbolic forms NumberStyles.Float admits
    [InlineData("Infinitypx")]
    [InlineData("Infinityem")]
    [InlineData("Infinityrem")]
    [InlineData("-Infinityem")]
    [InlineData("NaNpx")]
    [InlineData("NaNem")]
    // and through calc(), which recurses back into the same evaluation
    [InlineData("calc(1e400px)")]
    [InlineData("calc(1e400px + 1px)")]
    [InlineData("calc(Infinityem)")]
    public void ANonFiniteBorderWidthIsNotAWidth(string borderWidth) =>
        Assert.Equal("0", ClientTopFor(borderWidth));

    /// <summary>
    /// The neighbouring finite values still resolve, so the guard refuses what it cannot represent
    /// rather than anything that merely looks unusual. <c>1e2px</c> is the exponent form that is
    /// perfectly representable, and it must survive.
    /// </summary>
    [Theory]
    [InlineData("4px", "4")]
    [InlineData("1e1px", "10")]
    [InlineData("1e2px", "100")]
    [InlineData("0.5em", "8")]
    [InlineData("calc(2px + 3px)", "5")]
    public void AFiniteBorderWidthStillResolves(string borderWidth, string expected) =>
        Assert.Equal(expected, ClientTopFor(borderWidth));
}
