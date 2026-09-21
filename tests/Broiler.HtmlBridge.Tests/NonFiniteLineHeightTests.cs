namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A line height this component cannot represent must not reach geometry.
/// </summary>
/// <remarks>
/// <para>
/// <c>line-height</c> has a spelling no other length has: the unitless multiplier. It is the one
/// branch of the resolution that never reaches <c>TryEvaluateCssLengthWithViewport</c>, so the
/// finiteness exit that method was given does not cover it, and it is read with a bare
/// <c>NumberStyles.Float</c> parse — which accepts <c>Infinity</c> and <c>NaN</c> by name, and
/// which overflows a double on an exponent, or on a long enough run of digits, with no symbol in
/// the declaration at all.
/// </para>
/// <para>
/// The surface is a list-box <c>&lt;select&gt;</c>, whose scrolling area this component measures
/// itself rather than reading out of the layout snapshot: it is the option count times the line
/// height. So <c>line-height: 1e400</c> answered <c>scrollHeight === Infinity</c>, and
/// <c>line-height: NaN</c> answered <c>NaN</c>.
/// </para>
/// <para>
/// Half of this cannot be caught at the parse. <c>line-height: 1e307</c> is a multiplier a double
/// holds perfectly well; <c>16px</c> times it is not. The refusal is therefore on the resolved
/// height, where the value comes into existence, exactly as the transform round put it on the
/// assembled matrix rather than on each component.
/// </para>
/// <para>
/// <b>What refusing means here.</b> <c>normal</c> — the <c>1.2</c> factor this resolution already
/// uses for an unset line height — and deliberately not zero. A zero line height is a value a page
/// can write (<c>ATowerOfPreservedAnswers</c> pins it answering <c>96</c>), so substituting zero
/// would make a refusal indistinguishable from a declaration.
/// </para>
/// </remarks>
public class NonFiniteLineHeightTests
{
    private const string PageUrl = "https://example.test/non-finite-line-height";

    /// <summary>
    /// Six options in a <c>size="4"</c> list box at a pinned <c>16px</c> font, so every answer
    /// below is six rows: <c>normal</c> is 6 × 16 × 1.2, and the <c>96</c> some cases answer is
    /// six times the <c>16px</c> floor the row extent applies to a shorter line.
    /// </summary>
    private static string SelectPage(string lineHeight) =>
        "<!DOCTYPE html><html><head><style>" +
        "#s { font-size: 16px; line-height: " + lineHeight + "; }" +
        "</style></head><body>" +
        "<select id=\"s\" size=\"4\">" +
        "<option>a</option><option>b</option><option>c</option>" +
        "<option>d</option><option>e</option><option>f</option>" +
        "</select><div id=\"out\"></div></body></html>";

    private static string ScrollHeightFor(string lineHeight) =>
        PageProbe.OutOf(PageProbe.Render(
            [PageProbe.GuardedProbe("document.getElementById('s').scrollHeight")],
            SelectPage(lineHeight),
            PageUrl));

    /// <summary>What <c>normal</c> answers: 6 × 16 × 1.2, as JavaScript prints it.</summary>
    private const string Normal = "115.19999999999999";

    /// <summary>
    /// A multiplier that cannot be represented is not a multiplier, so the element is laid out at
    /// <c>normal</c> — the same answer the declaration's absence gives.
    /// </summary>
    [Theory]
    // the symbolic forms NumberStyles.Float admits by name
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("NaN")]
    // an exponent that overflows a double, with no symbol in the declaration
    [InlineData("1e400")]
    [InlineData("-1e400")]
    // and a multiplier that is representable while 16px times it is not, which is the half no
    // guard at the parse could have caught
    [InlineData("1e308")]
    public void AMultiplierThatCannotBeRepresentedIsNotAMultiplier(string lineHeight) =>
        Assert.Equal(Normal, ScrollHeightFor(lineHeight));

    /// <summary>
    /// The overflow needs no exponent and no symbol either: a run of digits longer than a double
    /// can hold is an entirely ordinary-looking declaration.
    /// </summary>
    [Fact]
    public void ARunOfDigitsTooLongForADoubleIsNotAMultiplierEither() =>
        Assert.Equal(Normal, ScrollHeightFor(new string('1', 401)));

    /// <summary>
    /// Every answer the refusal must not disturb, in one assertion so a change to any of them
    /// reads as the regression it is.
    /// </summary>
    /// <remarks>
    /// <c>0</c> and <c>-2</c> are the pair that makes zero the wrong refusal: both are values a
    /// page wrote, both resolve to a row shorter than the <c>16px</c> floor, and both answer
    /// <c>96</c>. <c>1e400px</c> is the same overflow in the length spelling, which the earlier
    /// round's exit already refuses — it answers <c>0</c> there and so takes the floor, and this
    /// change leaves that alone rather than promoting it to <c>normal</c>.
    /// </remarks>
    [Fact]
    public void ATowerOfPreservedAnswers() =>
        Assert.Equal(
            $"normal={Normal} unset={Normal} two=192 px=144 zero=96 negative=96 garbage=96 overflowingPx=96",
            $"normal={ScrollHeightFor("normal")}" +
            $" unset={ScrollHeightFor("inherit")}" +
            $" two={ScrollHeightFor("2")}" +
            $" px={ScrollHeightFor("24px")}" +
            $" zero={ScrollHeightFor("0")}" +
            $" negative={ScrollHeightFor("-2")}" +
            $" garbage={ScrollHeightFor("garbage")}" +
            $" overflowingPx={ScrollHeightFor("1e400px")}");

    /// <summary>
    /// One multiplication further out, and a route of its own: the scrolling area is the option
    /// count times the row extent, so a line height this component <em>can</em> represent still
    /// overflows once there are six rows of it. <c>line-height: 1e307</c> resolves to a finite
    /// <c>1.6e308</c> row and answered <c>scrollHeight === Infinity</c> off six of them.
    /// </summary>
    /// <remarks>
    /// The box is declared here, so the answer names what an unmeasurable scrolling area falls
    /// back to — the client extent, the box itself — rather than the <c>0</c> an element with no
    /// layout box reports for everything. The control beside it is a row extent that does not
    /// overflow, which still reaches past the box.
    /// </remarks>
    [Fact]
    public void AScrollingAreaThatOverflowsIsNoLargerThanTheBox()
    {
        var boxes = new Dictionary<string, System.Drawing.RectangleF>
        {
            ["s"] = new System.Drawing.RectangleF(0, 0, 120, 80),
        };

        string ScrollHeightWithBoxFor(string lineHeight)
        {
            var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
            {
                LayoutViewFactory = () => new DeclaredBoxLayoutView(boxes),
            }));

            var html = engine.Execute(
                [PageProbe.GuardedProbe("document.getElementById('s').scrollHeight")],
                SelectPage(lineHeight),
                PageUrl);

            Assert.NotNull(html);
            return PageProbe.OutOf(html!);
        }

        Assert.Equal(
            "overflows=80 representable=600",
            $"overflows={ScrollHeightWithBoxFor("1e307")}" +
            $" representable={ScrollHeightWithBoxFor("100px")}");
    }

    /// <summary>
    /// The second surface, and the one that shows the refusal is not a local patch on the select
    /// measurement: the <c>lh</c> unit resolves against the same line height, and a length built
    /// on a non-finite one was refused wholesale by the length exit — so the border vanished. With
    /// the line height refused where it is resolved, <c>1lh</c> is a readable length again.
    /// </summary>
    [Fact]
    public void ALengthInLhUnitsIsReadableAgainWhenTheLineHeightIsRefused()
    {
        var page =
            "<!DOCTYPE html><html><head><style>" +
            "#p { font-size: 16px; line-height: 1e400; }" +
            "#q { border-top-style: solid; border-top-width: 1lh; }" +
            "</style></head><body><div id=\"p\"><div id=\"q\">q</div></div>" +
            "<div id=\"out\"></div></body></html>";

        Assert.Equal(
            "19.2",
            PageProbe.OutOf(PageProbe.Render(
                [PageProbe.GuardedProbe("document.getElementById('q').clientTop")], page, PageUrl)));
    }
}
