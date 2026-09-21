using System.Globalization;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The same rule one level up, in the keyframe interpolator: a transform component this component
/// cannot represent is refused where the two keyframes are combined, so the page is left with one
/// of the two values it actually wrote rather than one this component invented.
/// </summary>
/// <remarks>
/// <para>
/// <c>element.animate()</c> bakes its snapshot-time value into the element's baked style overlay,
/// which is a serialize-time store: it is deliberately not <c>element.style</c> and not the
/// computed style, so the serialized document is the only place it can be read back — which is
/// what these assert on, and what a consumer of this component's output actually receives.
/// </para>
/// <para>
/// The producer here has no symbol in it at all and no exponent either. <c>TrySplitNumberUnit</c>
/// scans digits and hands them to <c>NumberStyles.Float</c>, and 401 digits overflow a double, so
/// interpolating half way to one wrote <c>transform: translateX(Infinitypx)</c> into the page.
/// </para>
/// </remarks>
public class NonFiniteKeyframeInterpolationTests
{
    private const string PageUrl = "https://example.test/non-finite-keyframes";

    /// <summary>A number with no exponent and no symbol that a double cannot hold all the same.</summary>
    private static readonly string OverflowingDigits = "1" + new string('0', 400);

    /// <summary>
    /// The <c>style</c> attribute <c>#t</c> is serialized with after an <c>animate()</c> from
    /// <c>translateX(0px)</c> to <paramref name="toTransform"/>, sampled at
    /// <paramref name="delayMs"/> (a negative delay lands the snapshot mid-animation).
    /// </summary>
    private static string BakedStyleOf(string toTransform, int delayMs)
    {
        var html = new ScriptEngine().Execute(
            [
                "document.getElementById('t').animate(" +
                "[{ transform: 'translateX(0px)' }, { transform: '" +
                toTransform.Replace("<overflow>", OverflowingDigits) + "' }], " +
                "{ duration: 100, delay: " + delayMs.ToString(CultureInfo.InvariantCulture) +
                ", fill: 'both' });",
            ],
            "<!DOCTYPE html><html><head><title>t</title></head><body>" +
            "<div id=\"t\"></div></body></html>",
            PageUrl);

        Assert.NotNull(html);

        var open = html!.IndexOf("<div id=\"t\"", StringComparison.Ordinal);
        Assert.True(open >= 0, $"no #t div in serialized output: {html}");
        var end = html.IndexOf('>', open);
        Assert.True(end >= 0, $"unterminated #t div in serialized output: {html}");

        return html[open..(end + 1)];
    }

    /// <summary>
    /// An endpoint that cannot be represented is not an endpoint, so the pair does not interpolate
    /// and the transform steps discretely — which is what a mismatched pair of units has always
    /// done here. Half way to it the page keeps the value it started from.
    /// </summary>
    [Fact]
    public void AnEndpointThatCannotBeRepresentedStepsDiscretelyInstead() =>
        Assert.Equal(
            "<div id=\"t\" style=\"transform: translateX(0px)\">",
            BakedStyleOf("translateX(<overflow>px)", -50));

    /// <summary>
    /// At the end of the animation discrete stepping lands on the page's own <c>to</c> value, verbatim.
    /// It is unrepresentable as a length, so the geometry that reads it back refuses it there —
    /// see <see cref="NonFiniteTransformTests"/> — but nothing here rewrites what the page wrote.
    /// </summary>
    [Fact]
    public void TheUnrepresentableEndpointItselfIsNotRewritten() =>
        Assert.Equal(
            "<div id=\"t\" style=\"transform: translateX(" + OverflowingDigits + "px)\">",
            BakedStyleOf("translateX(<overflow>px)", -100));

    /// <summary>
    /// PRESERVED: a representable pair still interpolates, at the midpoint and at the end. A guard
    /// that refused the arithmetic rather than its result would have taken these with it.
    /// </summary>
    [Theory]
    [InlineData("translateX(40px)", -50, "transform: translateX(20px)")]
    [InlineData("translateX(40px)", -100, "transform: translateX(40px)")]
    [InlineData("translateX(10px)", -25, "transform: translateX(2.5px)")]
    [InlineData("translateX(999999999px)", -100, "transform: translateX(999999999px)")]
    public void ARepresentablePairStillInterpolates(string toTransform, int delayMs, string expected) =>
        Assert.Equal(
            "<div id=\"t\" style=\"" + expected + "\">",
            BakedStyleOf(toTransform, delayMs));
}
