namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A frame dimension this component cannot represent must not become the frame's viewport.
/// </summary>
/// <remarks>
/// <para>
/// An <c>&lt;iframe&gt;</c> is sized by its inline <c>style</c> first and by its <c>width</c>/
/// <c>height</c> <em>content attribute</em> second, and only the first of those goes through the
/// length evaluator's finiteness exit. The attribute was read with a bare
/// <c>NumberStyles.Float</c> parse tested only <c>&gt; 0</c> — which is false for <c>NaN</c> but
/// true for <c>+∞</c>, so <c>width="1e400"</c> became the frame's viewport length and the
/// sub-document answered <c>documentElement.clientWidth === Infinity</c>.
/// </para>
/// <para>
/// There are three readers of a frame's viewport, and all three were open. The other two —
/// <c>ParseViewportDimensionAttribute</c> and <c>ExtractCssDimension</c> in
/// <c>DomBridgeUtils/Css.cs</c>, and <c>CascadedFrameViewport</c>'s own <c>ResolveFrameLength</c> —
/// feed the frame's <em>media queries</em>, and each guarded <c>!double.IsNaN</c> before converting
/// to <c>int</c>. A double-to-int conversion saturates rather than failing, so <c>+∞</c> left them
/// as <c>int.MaxValue</c>: a frame two billion pixels wide, matching every <c>min-width</c> query
/// its document could ask. <see cref="TheMediaQueryViewportIsNotClampedEither"/> reads that back.
/// </para>
/// <para>
/// <b>What refusing means here.</b> The default viewport — 1024 × 768 — which is what a frame with
/// no readable dimension has always fallen through to. Not zero: a zero-sized frame is a thing a
/// page can ask for, and the fall-through is the answer this method already gives for an attribute
/// it cannot read at all.
/// </para>
/// </remarks>
public class NonFiniteFrameViewportTests
{
    private const string PageUrl = "https://example.test/non-finite-frame-viewport";

    /// <summary>
    /// One <c>srcdoc</c> frame carrying <paramref name="frameAttributes"/>. The frame's own
    /// attributes are single-quoted so the double-quoted <c>srcdoc</c> survives, as in
    /// <see cref="FrameViewportInlineStyleTests"/>.
    /// </summary>
    private static string PageHtml(string frameAttributes, string frameHead = "", string frameBody = "") =>
        "<!DOCTYPE html><html><body>" +
        "<iframe id=\"f\" " + frameAttributes + " srcdoc=\"<!DOCTYPE html><html><head>" +
        frameHead + "</head><body>" + frameBody + "</body></html>\"></iframe>" +
        "<div id=\"out\"></div></body></html>";

    private static string Run(string page, string probe) =>
        PageProbe.OutOf(PageProbe.Render([PageProbe.GuardedProbe(probe)], page, PageUrl));

    /// <summary>The sub-document's own idea of how wide its viewport is.</summary>
    private static string ClientWidthOf(string frameAttributes) =>
        Run(PageHtml(frameAttributes),
            "document.getElementById('f').contentDocument.documentElement.clientWidth");

    private static string ClientHeightOf(string frameAttributes) =>
        Run(PageHtml(frameAttributes),
            "document.getElementById('f').contentDocument.documentElement.clientHeight");

    /// <summary>
    /// A width attribute that cannot be represented is not a width, so the frame's document is laid
    /// out in the default viewport — the same 1024 a frame with no width attribute gets.
    /// </summary>
    [Theory]
    [InlineData("width=\"1e400\"")]
    [InlineData("width=\"Infinity\"")]
    public void AWidthThatCannotBeRepresentedIsNotAWidth(string frameAttributes) =>
        Assert.Equal("1024", ClientWidthOf(frameAttributes));

    /// <summary>The height attribute is the same read on the other axis.</summary>
    [Theory]
    [InlineData("height=\"1e400\"")]
    [InlineData("height=\"Infinity\"")]
    public void AHeightThatCannotBeRepresentedIsNotAHeight(string frameAttributes) =>
        Assert.Equal("768", ClientHeightOf(frameAttributes));

    /// <summary>
    /// The overflow needs no exponent and no symbol: a run of digits longer than a double can hold
    /// is an ordinary-looking attribute, and it is the spelling a guard written against
    /// <c>NaN</c> and <c>Infinity</c> would still have admitted.
    /// </summary>
    [Fact]
    public void ARunOfDigitsTooLongForADoubleIsNotAWidthEither() =>
        Assert.Equal("1024", ClientWidthOf("width=\"" + new string('1', 401) + "\""));

    /// <summary>
    /// The second surface, and a different API: the sub-document's root box. The root's client
    /// rect <em>is</em> the viewport reference length on both axes, so the same attribute made
    /// <c>documentElement.getBoundingClientRect()</c> report an infinite box as well.
    /// </summary>
    [Fact]
    public void TheSubDocumentsRootBoxIsTheDefaultViewport() =>
        Assert.Equal(
            "0,0,1024,768",
            Run(
                PageHtml("width='1e400' height='1e400'"),
                "(function () { var b = document.getElementById('f').contentDocument" +
                ".documentElement.getBoundingClientRect();" +
                " return [b.left, b.top, b.width, b.height].join(','); })()"));

    /// <summary>
    /// The same declaration read through the frame's media queries, which come from three other
    /// parses entirely. A frame sized by an unrepresentable width used to match
    /// <c>(min-width: 1000000000px)</c> — because <c>(int)</c> of an infinity is <c>int.MaxValue</c>,
    /// not a failure — while <c>clientWidth</c> answered <c>Infinity</c> beside it.
    /// </summary>
    /// <remarks>
    /// Both spellings are here because they take different paths: the attribute is read by
    /// <c>ParseViewportDimensionAttribute</c> and the inline style falls through
    /// <c>ExtractCssDimension</c> to the cascade's own <c>ResolveFrameLength</c>. A representable
    /// <c>500px</c> frame is the control, and it still matches.
    /// </remarks>
    [Fact]
    public void TheMediaQueryViewportIsNotClampedEither()
    {
        const string MediaBody = "<div id='w'></div>";

        string MatchesAtLeast(string frameAttributes, string minWidth) =>
            Run(
                PageHtml(
                    frameAttributes,
                    frameHead: "<style media='(min-width: " + minWidth + ")'>#w{display:inline-block}</style>",
                    frameBody: MediaBody),
                "getComputedStyle(document.getElementById('f').contentDocument" +
                ".getElementById('w')).display === 'inline-block'");

        Assert.Equal(
            "attributeAtTwoBillion=false attributeAt400=false" +
            " styleAtTwoBillion=false styleAt400=false" +
            " representableAt400=true representableAtTwoBillion=false",
            $"attributeAtTwoBillion={MatchesAtLeast("width='1e400'", "2000000000px")}" +
            $" attributeAt400={MatchesAtLeast("width='1e400'", "400px")}" +
            $" styleAtTwoBillion={MatchesAtLeast("style='width: 1e400px'", "2000000000px")}" +
            $" styleAt400={MatchesAtLeast("style='width: 1e400px'", "400px")}" +
            $" representableAt400={MatchesAtLeast("width='900'", "400px")}" +
            $" representableAtTwoBillion={MatchesAtLeast("width='900'", "2000000000px")}");
    }

    /// <summary>
    /// Every answer the refusal must not disturb, including the three spellings that were already
    /// falling through at HEAD and are pinned here rather than claimed as fixes. <c>NaN</c>,
    /// <c>-1e400</c> and <c>-Infinity</c> all fail <c>&gt; 0</c>, which is exactly why that sign
    /// test could not be mistaken for a finiteness test: it caught one half of the overflow and
    /// admitted the other.
    /// </summary>
    [Fact]
    public void ATowerOfPreservedAnswers() =>
        Assert.Equal(
            "plain=300 nan=1024 negative=1024 negativeOverflow=1024 negativeInfinity=1024" +
            " garbage=1024 absent=1024 inlineStyle=250 overflowingStyle=1024",
            $"plain={ClientWidthOf("width=\"300\"")}" +
            $" nan={ClientWidthOf("width=\"NaN\"")}" +
            $" negative={ClientWidthOf("width=\"-5\"")}" +
            $" negativeOverflow={ClientWidthOf("width=\"-1e400\"")}" +
            $" negativeInfinity={ClientWidthOf("width=\"-Infinity\"")}" +
            $" garbage={ClientWidthOf("width=\"junk\"")}" +
            $" absent={ClientWidthOf("")}" +
            $" inlineStyle={ClientWidthOf("style='width: 250px'")}" +
            $" overflowingStyle={ClientWidthOf("style='width: 1e400px'")}");
}
