using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Which ancestor contains a <c>position: fixed</c> box, as the bridge's own <c>element.scrollParent()</c>
/// reports it — asserted from page script, written before the bridge's copy of the fixed-containing-block
/// predicate was swapped for the published <c>Broiler.CSS.CssContainingBlock.CreatedByTransformContainOrWillChange</c>.
/// <para>
/// <b>Why <c>scrollParent()</c> is the probe.</b> It is the one page-visible reader of that predicate:
/// modelled on the CSSOM View draft algorithm, it walks from a fixed box's containing block to the first
/// scroll container, and answers <c>null</c> when the fixed box has no containing block below the
/// initial one (it is then positioned against the viewport, which no element scrolls). So the answer
/// names the containing block indirectly: <c>root</c> (the scrolling element) or a scroller's id means
/// an ancestor contained the fixed box, <c>null</c> means none did.
/// </para>
/// <para>
/// <b>The bridge's copy lacked <c>will-change</c>.</b> CSS Will Change §2 says a <c>will-change</c> naming a
/// property whose non-initial value would create a fixed-position containing block must create one
/// itself, and Chromium does for <c>will-change: transform</c>. The copy tested only <c>transform</c> and
/// <c>contain</c>, so the first test failed against it. The second pins the rest of the predicate, above
/// all that a <em>positioned</em> ancestor is not a fixed box's containing block — the abs-pos
/// predicate the bridge also has (<c>DomBridgeUtils.EstablishesContainingBlock</c>) adds the
/// <c>position</c> keywords and must not stand in for this one. The third is about which ancestors the
/// predicate is asked of at all: like the layout engine, the walk passes over a non-atomic inline box.
/// </para>
/// </summary>
public class ScrollParentContainingBlockTests
{
    private const string PageUrl = "https://example.test/scroll-parent";

    /// <summary>
    /// Each fixed box sits alone under the ancestor its id names: <c>#fW</c> under a
    /// <c>will-change: transform</c> block in the flow, <c>#fWS</c> under a <c>will-change</c> list naming
    /// <c>transform</c> inside the <c>#sc</c> scroller, then one fixed box each under <c>transform</c>,
    /// <c>contain: paint</c>, <c>will-change: opacity</c> and a clipping <c>position: relative</c> block,
    /// and <c>#fN</c> with no candidate ancestor at all. Last, inside the <c>#sci</c> scroller, <c>#fWI</c>
    /// and <c>#fTI</c> sit under a <c>span</c> carrying <c>will-change: transform</c> and <c>transform</c>.
    /// </summary>
    private const string PageHtml =
        "<!DOCTYPE html><html><head><title>t</title></head><body>" +
        "<div id=\"wc\" style=\"will-change: transform\"><div id=\"fW\" style=\"position: fixed\"></div></div>" +
        "<div id=\"sc\" style=\"overflow: auto; height: 50px\">" +
        "<div id=\"wcs\" style=\"will-change: opacity, transform\"><div id=\"fWS\" style=\"position: fixed\"></div></div>" +
        "</div>" +
        "<div id=\"tf\" style=\"transform: translateX(10px)\"><div id=\"fT\" style=\"position: fixed\"></div></div>" +
        "<div id=\"ct\" style=\"contain: paint\"><div id=\"fC\" style=\"position: fixed\"></div></div>" +
        "<div id=\"wo\" style=\"will-change: opacity\"><div id=\"fO\" style=\"position: fixed\"></div></div>" +
        "<div id=\"rel\" style=\"position: relative; overflow: hidden\"><div id=\"fR\" style=\"position: fixed\"></div></div>" +
        "<div id=\"fN\" style=\"position: fixed\"></div>" +
        "<div id=\"sci\" style=\"overflow: auto; height: 50px\">" +
        "<span id=\"wci\" style=\"will-change: transform\"><div id=\"fWI\" style=\"position: fixed\"></div></span>" +
        "<span id=\"tfi\" style=\"transform: translateX(10px)\"><div id=\"fTI\" style=\"position: fixed\"></div></span>" +
        "</div>" +
        "<div id=\"out\"></div>" +
        "</body></html>";

    /// <summary>
    /// Names what <c>scrollParent()</c> answers for the element with that id: <c>null</c>, <c>root</c> for
    /// the document's scrolling element, or the scroller's own id. Run as its own script ahead of the probe.
    /// </summary>
    private const string Helpers = """
        function parentOf(id) {
          var p = document.getElementById(id).scrollParent();
          return p === null ? 'null' : p === document.documentElement ? 'root' : p.id;
        }
        """;

    /// <summary>
    /// As <see cref="NodeRelationshipCanonicalTests"/> does: a throw is written to <c>#out</c> too, so a
    /// missing member says so rather than leaving <c>#out</c> empty.
    /// </summary>
    private static string Run(string script) => PageProbe.RunGuarded(PageHtml, PageUrl, Helpers, script);

    [Fact]
    public void AFixedBoxUnderWillChangeTransformScrollsWithThatAncestorsScroller()
    {
        // What changed: `will-change: transform` now makes its element the fixed box's containing block,
        // exactly as `transform` itself does (CSS Will Change §2; Chromium agrees), because the bridge
        // calls CssContainingBlock.CreatedByTransformContainOrWillChange rather than its own copy, which
        // tested only transform and contain. So scrollParent() walks from that ancestor: to the scrolling
        // element for #fW, and to the #sc scroller around #wcs for #fWS, whose list merely includes
        // `transform`. Both answered null before — no containing block, so nothing scrolls the box.
        Assert.Equal(
            "willChange=root willChangeList=sc",
            Run("'willChange=' + parentOf('fW') + ' willChangeList=' + parentOf('fWS')"));
    }

    [Fact]
    public void OnlyTransformContainAndWillChangeTransformContainAFixedBox()
    {
        // A guard, unchanged by the cutover. `transform` and `contain: paint` contain a fixed box, so its
        // scrollParent() walks from them (nothing between them and the root scrolls). `will-change:
        // opacity` names no property that would. And a positioned ancestor — even a clipping one —
        // contains absolute boxes, not fixed ones: Chromium's containing block for #fR is the initial
        // one and its scrollParent is null, as it is for #fN with no candidate ancestor. A `relative`
        // answer other than null means the abs-pos predicate, which consults `position`, stood in.
        Assert.Equal(
            "transform=root contain=root willChangeOpacity=null relative=null none=null",
            Run("'transform=' + parentOf('fT') + ' contain=' + parentOf('fC') + " +
                "' willChangeOpacity=' + parentOf('fO') + ' relative=' + parentOf('fR') + ' none=' + parentOf('fN')"));
    }

    [Fact]
    public void ANonAtomicInlineAncestorDoesNotContainAFixedBox()
    {
        // What changed: the walk passes over a non-atomic inline box before asking the predicate, as the
        // layout engine's fixed-position path does. A transform does not apply to a `display: inline`
        // span (CSS Transforms 1), so neither it nor a will-change hint for it makes the span a fixed
        // box's containing block; Chromium positions both boxes against the viewport and their
        // scrollParent() is null. Before, the span under `transform` contained #fTI, and after the swap
        // to the canonical predicate the span under `will-change: transform` contained #fWI too, so both
        // answered the #sci scroller around them.
        Assert.Equal(
            "willChangeInline=null transformInline=null",
            Run("'willChangeInline=' + parentOf('fWI') + ' transformInline=' + parentOf('fTI')"));
    }
}
