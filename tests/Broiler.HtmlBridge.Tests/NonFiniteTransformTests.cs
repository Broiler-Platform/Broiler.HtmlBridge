using System.Drawing;

using Broiler.Dom;
using Broiler.HtmlBridge;
using Broiler.Layout;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A transform component this component cannot represent must not reach geometry: the function it
/// was written in is not a transform, so it contributes the identity and the element keeps its
/// untransformed border box — the same rect <c>transform: none</c> answers.
/// </summary>
/// <remarks>
/// <para>
/// The two producers are the ones <see cref="NonFiniteLengthTests"/> describes: .NET's
/// <c>NumberStyles.Float</c> accepts the symbolic spellings <c>NaN</c>/<c>Infinity</c>, and an
/// exponent overflows a double with no symbol in it at all, so <c>translateX(1e400px)</c> is an
/// ordinary-looking declaration. The length evaluator was closed one round earlier; the transform
/// parser in <c>Animations.cs</c> had no finiteness test anywhere in the file.
/// </para>
/// <para>
/// The damage compounds, which is why these read the whole rect rather than one edge: an infinite
/// matrix component makes all four corners infinite, and <c>maxX - minX</c> over two infinities is
/// <c>NaN</c> — so one bad declaration answered <c>left === Infinity</c> <em>and</em>
/// <c>width === NaN</c> to the same page.
/// </para>
/// </remarks>
public class NonFiniteTransformTests
{
    private const string PageUrl = "https://example.test/non-finite-transform";

    /// <summary>A box at the origin, so a translation reads straight off <c>left</c>/<c>top</c>.</summary>
    private static readonly Dictionary<string, RectangleF> ProbeBoxes = new()
    {
        ["t"] = new RectangleF(0, 0, 100, 50),
    };

    /// <summary>The untransformed border box of <c>#t</c>, which is what a refused transform leaves.</summary>
    private const string UntransformedRect = "0,0,100,50";

    /// <summary>
    /// <c>#t</c>'s <c>getBoundingClientRect()</c> as <c>left,top,width,height</c>, with
    /// <paramref name="style"/> on the element itself.
    /// </summary>
    private static string RectOf(string style) =>
        RectOfMarkup($"<div id=\"t\" style=\"{style}\"></div>");

    /// <summary>
    /// <c>#t</c>'s <c>getBoundingClientRect()</c> as <c>left,top,width,height</c> after
    /// <paramref name="probeMarkup"/> is parsed as the page's only content besides the probe.
    /// </summary>
    private static string RectOfMarkup(string probeMarkup)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = static () => new FixedBoxLayoutView(ProbeBoxes),
        }));

        var html = engine.Execute(
            [PageProbe.GuardedProbe(
                "(function () { var r = document.getElementById('t').getBoundingClientRect(); " +
                "return [r.left, r.top, r.width, r.height].join(','); })()")],
            "<!DOCTYPE html><html><head><title>t</title></head><body>" +
            probeMarkup + "<div id=\"out\"></div></body></html>",
            PageUrl);

        Assert.NotNull(html);

        return PageProbe.OutOf(html!);
    }

    /// <summary>
    /// A translation whose length cannot be represented is not a translation. Before this, the
    /// infinity went straight into the corner coordinates: <c>left</c> was <c>Infinity</c> and
    /// <c>width</c> was <c>NaN</c>.
    /// </summary>
    [Theory]
    [InlineData("transform: translate(1e400px, 0)")]
    [InlineData("transform: translate(0, 1e400px)")]
    [InlineData("transform: translate(-1e400px, 0)")]
    [InlineData("transform: translateX(1e400px)")]
    [InlineData("transform: translateY(1e400px)")]
    [InlineData("transform: translateX(Infinitypx)")]
    [InlineData("transform: translateY(-Infinitypx)")]
    [InlineData("transform: translateX(NaNpx)")]
    // a bare number in translate() is not a length and was already ignored; the symbolic
    // spellings of one are the same question asked without a unit.
    [InlineData("transform: translateX(1e400)")]
    public void ATranslationThatCannotBeRepresentedIsNotATranslation(string style) =>
        Assert.Equal(UntransformedRect, RectOf(style));

    /// <summary>
    /// The percentage form resolves against the box rather than being taken as pixels, so it
    /// overflows on its own arithmetic as well as on its own spelling.
    /// </summary>
    [Theory]
    [InlineData("transform: translate(1e400%, 0)")]
    [InlineData("transform: translate(0, 1e400%)")]
    [InlineData("transform: translate(Infinity%, 0)")]
    [InlineData("transform: translate(NaN%, 0)")]
    [InlineData("transform: translateX(-Infinity%)")]
    public void APercentageTranslationThatCannotBeRepresentedIsNotATranslation(string style) =>
        Assert.Equal(UntransformedRect, RectOf(style));

    /// <summary>
    /// One unrepresentable component makes the whole <c>matrix()</c> unrepresentable — every
    /// corner is mapped through all six, so there is no "only this term" about it.
    /// </summary>
    [Theory]
    [InlineData("transform: matrix(1e400, 0, 0, 1, 0, 0)")]
    [InlineData("transform: matrix(1, 1e400, 0, 1, 0, 0)")]
    [InlineData("transform: matrix(1, 0, 1e400, 1, 0, 0)")]
    [InlineData("transform: matrix(1, 0, 0, 1e400, 0, 0)")]
    [InlineData("transform: matrix(1, 0, 0, 1, 1e400, 0)")]
    [InlineData("transform: matrix(1, 0, 0, 1, 0, 1e400)")]
    [InlineData("transform: matrix(Infinity, 0, 0, 1, 0, 0)")]
    [InlineData("transform: matrix(NaN, 0, 0, 1, 0, 0)")]
    public void AMatrixWithAComponentThatCannotBeRepresentedIsNotAMatrix(string style) =>
        Assert.Equal(UntransformedRect, RectOf(style));

    /// <summary>
    /// An angle that cannot be represented reaches geometry through <c>Math.Cos</c> and
    /// <c>Math.Tan</c>, which answer <c>NaN</c> for it — so the rect came back <c>NaN</c> on all
    /// four numbers rather than infinite.
    /// </summary>
    [Theory]
    [InlineData("transform: rotate(1e400deg)")]
    [InlineData("transform: rotate(NaNdeg)")]
    [InlineData("transform: rotate(Infinitydeg)")]
    [InlineData("transform: rotate(-Infinitydeg)")]
    [InlineData("transform: rotate(1e400rad)")]
    [InlineData("transform: rotate(Infinitygrad)")]
    [InlineData("transform: rotate(1e400turn)")]
    // the unitless form, which this parser reads as degrees
    [InlineData("transform: rotate(Infinity)")]
    [InlineData("transform: rotateZ(1e400deg)")]
    [InlineData("transform: skewX(1e400deg)")]
    [InlineData("transform: skewY(NaNdeg)")]
    public void AnAngleThatCannotBeRepresentedIsNotAnAngle(string style) =>
        Assert.Equal(UntransformedRect, RectOf(style));

    /// <summary>
    /// A scale factor that cannot be represented is not a scale factor, in either the number or
    /// the percentage spelling.
    /// </summary>
    [Theory]
    [InlineData("transform: scale(1e400)")]
    [InlineData("transform: scale(NaN)")]
    [InlineData("transform: scale(Infinity)")]
    [InlineData("transform: scale(1, 1e400)")]
    [InlineData("transform: scale(1e400%)")]
    [InlineData("transform: scaleX(1e400)")]
    [InlineData("transform: scaleY(Infinity)")]
    [InlineData("transform: scaleX(NaN%)")]
    public void AScaleFactorThatCannotBeRepresentedIsNotAScaleFactor(string style) =>
        Assert.Equal(UntransformedRect, RectOf(style));

    /// <summary>
    /// The refusal is per function, which is this parser's established granularity for what it
    /// cannot model — so the rest of the list still applies. <c>translateY(20px)</c> beside a
    /// refused <c>translateX</c> still moves the box 20px down.
    /// </summary>
    [Theory]
    [InlineData("transform: translateX(1e400px) translateY(20px)", "0,20,100,50")]
    [InlineData("transform: translateY(20px) translateX(Infinitypx)", "0,20,100,50")]
    [InlineData("transform: scale(NaN) translateY(20px)", "0,20,100,50")]
    public void ARefusedFunctionDoesNotTakeTheRestOfTheListWithIt(string style, string expected) =>
        Assert.Equal(expected, RectOf(style));

    /// <summary>
    /// A composition that overflows although every function in it is representable on its own —
    /// <c>1e200 × 1e200</c> is an infinity no single factor was — is refused at the composed
    /// matrix, which is the second place the finiteness test is read.
    /// </summary>
    [Fact]
    public void ACompositionThatOverflowsIsNotATransform() =>
        Assert.Equal(UntransformedRect, RectOf("transform: scale(1e200) scale(1e200)"));

    /// <summary>
    /// An ancestor's refused transform is refused the same way: the chain walks it and it
    /// contributes nothing, leaving the descendant's own box.
    /// </summary>
    [Fact]
    public void AnAncestorsRefusedTransformContributesNothing() =>
        Assert.Equal(
            UntransformedRect,
            RectOfMarkup(
                "<div style=\"transform: translateX(1e400px)\"><div id=\"t\"></div></div>"));

    /// <summary>
    /// The <c>transform-origin</c> the chain rotates about is resolved by the shared grammar, not
    /// by the transform parser, so an unrepresentable one arrives past every guard above and
    /// infects the corners on its own. The chain's own exit is what refuses it.
    /// </summary>
    [Theory]
    [InlineData("transform: rotate(90deg); transform-origin: 1e400px 0")]
    [InlineData("transform: rotate(90deg); transform-origin: 0 1e400px")]
    [InlineData("transform: scale(2); transform-origin: 1e400px 1e400px")]
    public void AnOriginThatCannotBeRepresentedLeavesTheBoxUntransformed(string style) =>
        Assert.Equal(UntransformedRect, RectOf(style));

    /// <summary>
    /// PRESERVED: the representable transforms, which must go on answering exactly what they did.
    /// A guard that refused anything unusual rather than anything unrepresentable would take
    /// <c>1e2px</c> — the exponent form that is a perfectly ordinary 100 — with it.
    /// </summary>
    [Theory]
    [InlineData("transform: none", "0,0,100,50")]
    [InlineData("transform: translateY(20px)", "0,20,100,50")]
    [InlineData("transform: translateX(10px) translateY(40px)", "10,40,100,50")]
    [InlineData("transform: translateY(1e2px)", "0,100,100,50")]
    [InlineData("transform: translate(10px, 20px)", "10,20,100,50")]
    [InlineData("transform: translateX(50%)", "50,0,100,50")]
    [InlineData("transform: matrix(1, 0, 0, 1, 10, 20)", "10,20,100,50")]
    [InlineData("transform: scale(2)", "-50,-25,200,100")]
    [InlineData("transform: scale(50%)", "25,12.5,50,25")]
    public void ARepresentableTransformIsUnchanged(string style, string expected) =>
        Assert.Equal(expected, RectOf(style));

    /// <summary>
    /// PRESERVED, and the rule this must not be confused with: a component the parser cannot
    /// <em>resolve</em> — a <c>calc()</c> it has no evaluator for — still contributes zero while
    /// the rest of the function survives, which is the behaviour
    /// <c>DependencyAlignmentTransformSplitTests</c> pins and the reason the dependency's own
    /// resolver was rejected. Unresolvable and unrepresentable are different questions: the first
    /// has a defined fallback, the second has no number at all.
    /// </summary>
    [Theory]
    [InlineData("transform: translate(calc(100% - 10px), 20px)", "0,20,100,50")]
    [InlineData("transform: translateX(calc(1px + 1px)) translateY(30px)", "0,30,100,50")]
    [InlineData("transform: translateY(nonsense)", "0,0,100,50")]
    public void AnUnresolvableComponentStillContributesZero(string style, string expected) =>
        Assert.Equal(expected, RectOf(style));

    /// <summary>
    /// A layout view that lays nothing out: an element whose <c>id</c> is in the map gets exactly
    /// that rectangle at all three box-model levels and every other element gets no box. What is
    /// under test is which matrix the bridge builds out of a transform value, and the chain needs
    /// a box to apply it to.
    /// </summary>
    private sealed class FixedBoxLayoutView : ILayoutView
    {
        private readonly IReadOnlyDictionary<string, RectangleF> _borderBoxesById;

        internal FixedBoxLayoutView(IReadOnlyDictionary<string, RectangleF> borderBoxesById) =>
            _borderBoxesById = borderBoxesById;

        public IReadOnlyDictionary<DomElement, BoxGeometry> GetGeometry(
            DomDocument document, SizeF viewport, string baseUrl,
            Func<DomElement, DomDocument?>? contentDocumentResolver = null)
        {
            var geometry = new Dictionary<DomElement, BoxGeometry>(ReferenceEqualityComparer.Instance);
            Collect(document, geometry);
            return geometry;
        }

        public void Dispose()
        {
        }

        private void Collect(DomNode node, Dictionary<DomElement, BoxGeometry> geometry)
        {
            foreach (var element in node.ChildElements)
            {
                if (element.GetAttributeByQualifiedName("id") is { } id &&
                    _borderBoxesById.TryGetValue(id, out var borderBox))
                {
                    geometry[element] = new BoxGeometry(borderBox, borderBox, borderBox);
                }

                Collect(element, geometry);
            }
        }
    }
}
