using System.Drawing;

using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The document this component hands back carries no length it cannot represent, on the two
/// surfaces that compute one at serialization time rather than reading one back: the SVG
/// attributes a used zoom rescales, and the track placeholder a <c>&lt;progress&gt;</c> is given.
/// </summary>
/// <remarks>
/// <para>
/// Both are the shape <see cref="NonFiniteUsedZoomTests"/> found for the zoom bake and
/// <see cref="NonFiniteKeyframeInterpolationTests"/> for the animated one: not a wrong number a
/// script reads, but markup a consumer receives with a value the page never wrote in it. Both are
/// also a <em>multiplication</em> rather than a spelling — the used zoom is finite (the earlier
/// round refuses one that is not) and the attribute can be too, and the product still need not be,
/// so no guard at either parse could have caught them.
/// </para>
/// <para>
/// The answer in each case is the one the same code already gives when it cannot scale or cannot
/// read: the SVG attribute is left exactly as the page wrote it, and the track takes its default
/// length. The preserved cases pin both, so the refusal is demonstrably the existing path.
/// </para>
/// </remarks>
public class NonFiniteSerializedLengthTests
{
    private const string PageUrl = "https://example.test/non-finite-serialized-length";

    private static readonly Dictionary<string, RectangleF> ProbeBoxes = new()
    {
        ["v"] = new RectangleF(0, 0, 200, 100),
        ["p"] = new RectangleF(0, 0, 100, 20),
    };

    private static string Render(string body)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = static () => new DeclaredBoxLayoutView(ProbeBoxes),
        }));

        var html = engine.Execute(["1;"], body, PageUrl);

        Assert.NotNull(html);

        return html!;
    }

    /// <summary>The opening tag with <paramref name="id"/> as the serialized document carries it.</summary>
    private static string TagOf(string html, string id)
    {
        var marker = $"id=\"{id}\"";
        var at = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, $"no element with id=\"{id}\" in serialized output: {html}");
        var start = html.LastIndexOf('<', at);
        var end = html.IndexOf('>', at);
        Assert.True(start >= 0 && end >= 0, $"unterminated tag for id=\"{id}\": {html}");
        return html[start..(end + 1)];
    }

    private static string SvgAt(string zoom, string shape) =>
        Render(
            "<!DOCTYPE html><html><head><style>#v { zoom: " + zoom + "; }</style></head><body>" +
            $"<svg id=\"v\">{shape}</svg></body></html>");

    /// <summary>
    /// A geometry attribute whose scaled value cannot be represented is left as the page wrote it.
    /// Before this the serialized document carried <c>width="Infinity"</c> — from an attribute a
    /// double holds perfectly well and a zoom of 2.
    /// </summary>
    [Theory]
    [InlineData("1e308", "width")]
    [InlineData("1e400", "width")]
    public void AnAttributeWhoseScaledValueOverflowsIsLeftAsWritten(string width, string attribute)
    {
        var tag = TagOf(SvgAt("2", $"<rect id=\"r\" x=\"0\" y=\"0\" width=\"{width}\" height=\"1\"></rect>"), "r");

        Assert.Contains($"{attribute}=\"{width}\"", tag);
        Assert.DoesNotContain("Infinity", tag);
        Assert.DoesNotContain("NaN", tag);
    }

    /// <summary>The same through a unit, where the scaled value keeps the unit it was written in.</summary>
    [Fact]
    public void AUnitBearingAttributeWhoseScaledValueOverflowsIsLeftAsWritten()
    {
        var tag = TagOf(SvgAt("2", "<rect id=\"r\" x=\"0\" y=\"0\" width=\"1e308px\" height=\"1\"></rect>"), "r");

        Assert.Contains("width=\"1e308px\"", tag);
        Assert.DoesNotContain("Infinity", tag);
    }

    /// <summary>
    /// And through path data and a points list, whose numbers are rescaled one match at a time —
    /// so the refusal has to be per number, leaving the readable ones scaled.
    /// </summary>
    [Fact]
    public void APathNumberWhoseScaledValueOverflowsIsLeftAsWritten()
    {
        var tag = TagOf(SvgAt("2", "<path id=\"r\" d=\"M 1e308 5 L 10 10\"></path>"), "r");

        Assert.Contains("1e308", tag);
        Assert.Contains("20 20", tag);
        Assert.DoesNotContain("Infinity", tag);
    }

    /// <summary>
    /// What the SVG refusal must not disturb: at the same zoom, a representable attribute is still
    /// scaled, a percentage and an unreadable token are still left alone, and an unzoomed document
    /// still carries what the page wrote.
    /// </summary>
    [Fact]
    public void ARepresentableAttributeIsStillScaled()
    {
        Assert.Contains(
            "width=\"200\"",
            TagOf(SvgAt("2", "<rect id=\"r\" x=\"0\" y=\"0\" width=\"100\" height=\"1\"></rect>"), "r"));
        Assert.Contains(
            "width=\"200\"",
            TagOf(SvgAt("2", "<rect id=\"r\" x=\"0\" y=\"0\" width=\"1e2\" height=\"1\"></rect>"), "r"));
        Assert.Contains(
            "width=\"50%\"",
            TagOf(SvgAt("2", "<rect id=\"r\" x=\"0\" y=\"0\" width=\"50%\" height=\"1\"></rect>"), "r"));
        Assert.Contains(
            "width=\"junk\"",
            TagOf(SvgAt("2", "<rect id=\"r\" x=\"0\" y=\"0\" width=\"junk\" height=\"1\"></rect>"), "r"));
        Assert.Contains(
            "d=\"M 40 10 L 20 20\"",
            TagOf(SvgAt("2", "<path id=\"r\" d=\"M 20 5 L 10 10\"></path>"), "r"));
    }

    private static string ProgressFill(string style)
    {
        var html = Render(
            "<!DOCTYPE html><html><head><title>t</title></head><body>" +
            $"<progress id=\"p\" value=\"0.5\" style=\"{style}\"></progress></body></html>");

        var at = html.IndexOf("<div style=\"position: absolute", StringComparison.Ordinal);
        Assert.True(at >= 0, $"no track placeholder in serialized output: {html}");
        return html[at..(html.IndexOf('>', at) + 1)];
    }

    /// <summary>
    /// A track length that cannot be represented is the default track length, not an infinity
    /// written into the document. Before this the placeholder carried <c>width: Infinitypx</c>.
    /// </summary>
    [Theory]
    [InlineData("width: 1e400px")]
    [InlineData("width: Infinitypx")]
    [InlineData("width: -1e400px")]
    public void ATrackLengthThatCannotBeRepresentedIsTheDefaultTrack(string style)
    {
        var fill = ProgressFill(style);

        Assert.Contains("width: 60px", fill);
        Assert.DoesNotContain("Infinity", fill);
        Assert.DoesNotContain("NaN", fill);
    }

    /// <summary>
    /// What the track refusal must not disturb: a readable width still sizes the track, and the
    /// three values that were already unreadable still answer the same default a refused one does.
    /// </summary>
    [Fact]
    public void ATowerOfPreservedTrackAnswers()
    {
        Assert.Contains("width: 50px", ProgressFill("width: 100px"));
        Assert.Contains("width: 60px", ProgressFill("width: junk"));
        Assert.Contains("width: 60px", ProgressFill("width: auto"));
        Assert.Contains("width: 60px", ProgressFill("color: red"));
    }
}
