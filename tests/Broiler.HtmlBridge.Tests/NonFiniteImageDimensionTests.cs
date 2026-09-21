namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// An image dimension this component cannot represent must not reach <c>img.width</c> /
/// <c>img.height</c>.
/// </summary>
/// <remarks>
/// <para>
/// The getter reads the used dimension out of computed style, falls back to the <c>width</c>/
/// <c>height</c> content attribute, and then to <c>0</c>. Both reads were open, and for the same
/// reason as everything else in this round: the CSS one guarded <c>!double.IsNaN</c>, which is
/// true of <c>+∞</c>, and the attribute one guarded nothing at all. So
/// <c>&lt;img style="width: 1e400px"&gt;</c> and <c>&lt;img width="1e400"&gt;</c> both answered
/// <c>img.width === Infinity</c>, and <c>width="NaN"</c> answered <c>NaN</c> — off a declaration
/// with no symbol in it in the first two cases.
/// </para>
/// <para>
/// <b>What refusing means here.</b> The next link in the chain the getter already documents. A
/// computed value it cannot represent falls through to the content attribute exactly as an
/// unreadable one does — <see cref="ARefusedComputedValueStillFallsToTheAttribute"/> is that,
/// read from a page — and an attribute it cannot represent falls through to <c>0</c>. Neither is
/// a number the guard invented; they are the only two answers this getter has ever had.
/// </para>
/// </remarks>
public class NonFiniteImageDimensionTests
{
    private const string PageUrl = "https://example.test/non-finite-image-dimension";

    private static string ImagePage(string attributes) =>
        "<!DOCTYPE html><html><body><img id=\"i\" " + attributes + ">" +
        "<div id=\"out\"></div></body></html>";

    private static string WidthOf(string attributes) =>
        PageProbe.OutOf(PageProbe.Render(
            [PageProbe.GuardedProbe("document.getElementById('i').width")],
            ImagePage(attributes),
            PageUrl));

    private static string HeightOf(string attributes) =>
        PageProbe.OutOf(PageProbe.Render(
            [PageProbe.GuardedProbe("document.getElementById('i').height")],
            ImagePage(attributes),
            PageUrl));

    /// <summary>
    /// A CSS width that cannot be represented is not a width, so the getter takes its documented
    /// fallback — here the last one, <c>0</c>, because the image carries no width attribute either.
    /// </summary>
    [Theory]
    [InlineData("style=\"width: 1e400px\"")]
    [InlineData("style=\"width: -1e400px\"")]
    [InlineData("style=\"width: 1e400em\"")]
    public void ACssWidthThatCannotBeRepresentedIsNotAWidth(string attributes) =>
        Assert.Equal("0", WidthOf(attributes));

    /// <summary>The same read on the other axis.</summary>
    [Fact]
    public void ACssHeightThatCannotBeRepresentedIsNotAHeight() =>
        Assert.Equal("0", HeightOf("style=\"height: 1e400px\""));

    /// <summary>
    /// The content attribute is the second read and was open on its own: it had no finiteness test
    /// at all, so it admitted the overflow and the <c>NaN</c> spelling alike.
    /// </summary>
    [Theory]
    [InlineData("width=\"1e400\"")]
    [InlineData("width=\"-1e400\"")]
    [InlineData("width=\"NaN\"")]
    public void AnAttributeWidthThatCannotBeRepresentedIsNotAWidth(string attributes) =>
        Assert.Equal("0", WidthOf(attributes));

    /// <summary>The attribute read on the other axis.</summary>
    [Fact]
    public void AnAttributeHeightThatCannotBeRepresentedIsNotAHeight() =>
        Assert.Equal("0", HeightOf("height=\"1e400\""));

    /// <summary>
    /// Neither overflow needs an exponent or a symbol: a run of digits longer than a double can
    /// hold is an ordinary-looking value in both spellings, and it is what a guard written against
    /// <c>NaN</c> and <c>Infinity</c> would still have let through.
    /// </summary>
    [Fact]
    public void ARunOfDigitsTooLongForADoubleIsNotAWidthEither()
    {
        var digits = new string('1', 401);

        Assert.Equal(
            "css=0 attribute=0",
            $"css={WidthOf($"style=\"width: {digits}px\"")}" +
            $" attribute={WidthOf($"width=\"{digits}\"")}");
    }

    /// <summary>
    /// The fallback chain, which is the whole reason the refusal is at each read rather than at one
    /// exit. A computed width this component cannot represent leaves the attribute beside it
    /// readable, so the page gets the <c>40</c> it wrote rather than the <c>0</c> a single exit
    /// would have collapsed to. The control is the same markup with the CSS value spelled
    /// <c>garbage</c>, which has always answered that way.
    /// </summary>
    [Fact]
    public void ARefusedComputedValueStillFallsToTheAttribute() =>
        Assert.Equal(
            "overflowing=40 symbolic=40 unreadable=40 representable=30",
            $"overflowing={WidthOf("style=\"width: 1e400px\" width=\"40\"")}" +
            $" symbolic={WidthOf("style=\"width: Infinitypx\" width=\"40\"")}" +
            $" unreadable={WidthOf("style=\"width: garbage\" width=\"40\"")}" +
            $" representable={WidthOf("style=\"width: 30px\" width=\"40\"")}");

    /// <summary>
    /// Every answer the refusal must not disturb, so that a change to any of them reads as the
    /// regression it is.
    /// </summary>
    [Fact]
    public void ATowerOfPreservedAnswers() =>
        Assert.Equal(
            "bare=0 css=30 attribute=40 junkAttribute=0 zeroAttribute=0 negativeAttribute=-5 cssHeight=25",
            $"bare={WidthOf("")}" +
            $" css={WidthOf("style=\"width: 30px\"")}" +
            $" attribute={WidthOf("width=\"40\"")}" +
            $" junkAttribute={WidthOf("width=\"junk\"")}" +
            $" zeroAttribute={WidthOf("width=\"0\"")}" +
            $" negativeAttribute={WidthOf("width=\"-5\"")}" +
            $" cssHeight={HeightOf("style=\"height: 25px\"")}");
}
