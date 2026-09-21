using System.Globalization;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The <c>&lt;img&gt;</c> <c>width</c>/<c>height</c> content attribute reads the same on every
/// machine.
/// </summary>
/// <remarks>
/// <para>
/// It used to be read with the parameterless <c>double.TryParse</c>, which uses the <em>current
/// culture</em>. A content attribute is not a localised number — HTML parses it with no culture at
/// all — so the same document answered differently depending on where it was opened. On a German
/// machine, where the group separator is <c>.</c> and the decimal separator is <c>,</c>,
/// <c>width="1.234"</c> answered <c>1234</c> and <c>width="1,5"</c> answered <c>1.5</c>. The first
/// is the dangerous one: it is a thousandfold error that a page author on an English machine would
/// never see, and it grows with the number of digits in the group.
/// </para>
/// <para>
/// These cases are written so they mean something on <em>any</em> machine. A test that only asserted
/// <c>"1.5" → 1.5</c> would pass under the old code on an invariant-like culture and fail under it
/// here, which makes it a test of the runner rather than of the component; the culture is therefore
/// stated in the assertion message, and the comma case pins the direction the confusion runs.
/// </para>
/// <para>
/// This pins the culture, not HTML's integer grammar. A browser applies the rules for parsing
/// non-negative integers and answers <c>1</c> for <c>width="1.5"</c>, where this answers
/// <c>1.5</c>; that gap is recorded as its own open item rather than settled here.
/// </para>
/// </remarks>
public class ImageDimensionCultureTests
{
    private const string PageUrl = "https://example.test/image-dimension-culture";

    private static string WidthOf(string attributeValue) =>
        PageProbe.OutOf(PageProbe.Render(
            [PageProbe.GuardedProbe("document.getElementById('i').width")],
            "<!DOCTYPE html><html><body><img id=\"i\" src=\"x.png\" width=\"" + attributeValue + "\">" +
            "<div id=\"out\"></div></body></html>",
            PageUrl));

    private static string Because(string attributeValue, string expected) =>
        $"width=\"{attributeValue}\" must read as {expected} under any culture; this run is " +
        $"'{CultureInfo.CurrentCulture.Name}', whose decimal separator is " +
        $"'{CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator}' and group separator is " +
        $"'{CultureInfo.CurrentCulture.NumberFormat.NumberGroupSeparator}'.";

    /// <summary>
    /// A dot is a decimal point, never a group separator. Under the current culture on a German
    /// machine the old read answered 15 and 1234 for these.
    /// </summary>
    [Theory]
    [InlineData("1.5", "1.5")]
    [InlineData("1.234", "1.234")]
    [InlineData("12.75", "12.75")]
    [InlineData("0.5", "0.5")]
    public void ADotIsADecimalPointWhateverTheMachineThinks(string attributeValue, string expected) =>
        Assert.Equal(expected, WidthOf(attributeValue));

    /// <summary>
    /// A comma is not a decimal point, so the attribute is not a number and the getter takes its
    /// documented last fallback. The old read answered 1.5 for the first of these on a German
    /// machine — the same value the correct answer to <c>width="1.5"</c> is, from the opposite
    /// spelling, which is what made the confusion hard to see.
    /// </summary>
    [Theory]
    [InlineData("1,5")]
    [InlineData("1,234")]
    public void ACommaIsNotADecimalPoint(string attributeValue) =>
        Assert.Equal("0", WidthOf(attributeValue));

    /// <summary>The ordinary spellings are unaffected, which is most of what this attribute carries.</summary>
    [Theory]
    [InlineData("100", "100")]
    [InlineData("12", "12")]
    [InlineData("0", "0")]
    public void APlainIntegerIsUnaffected(string attributeValue, string expected) =>
        Assert.Equal(expected, WidthOf(attributeValue));

    /// <summary>
    /// The two spellings stated together, with the culture in the failure message, so a run on a
    /// machine where this cannot fail still says which culture it proved nothing about.
    /// </summary>
    [Fact]
    public void TheDotAndCommaSpellingsDoNotSwapMeaning()
    {
        Assert.Equal("1.5", WidthOf("1.5"));
        Assert.Equal("0", WidthOf("1,5"));
        Assert.True(WidthOf("1.234") == "1.234", Because("1.234", "1.234"));
    }
}
