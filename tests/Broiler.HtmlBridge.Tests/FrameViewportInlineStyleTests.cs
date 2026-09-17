using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The viewport a frame's document sees when the <c>&lt;iframe&gt;</c> is sized by its own inline
/// <c>style</c> attribute — asserted from page script through the frame's media queries, written before
/// the bridge's substring search of that attribute was replaced by a real declaration lookup.
/// <para>
/// <b>What the frame's viewport is.</b> A frame's document is laid out in the frame's content box, so its
/// <c>@media</c>/<c>media=""</c> width and height features, its viewport units and the backdrop the bridge
/// synthesizes for a modal dialog in it all resolve against the frame's used <c>width</c>/<c>height</c>.
/// The bridge reads those from the iframe's inline style first, then its <c>width</c>/<c>height</c>
/// attributes, then the cascade.
/// </para>
/// <para>
/// <b>What the substring search got wrong.</b> It looked for the first occurrence of <c>width</c> or
/// <c>height</c> anywhere in the attribute text and took the value after the next colon. That found
/// <c>width</c> inside <c>max-width</c> and <c>border-width</c> and <c>height</c> inside
/// <c>line-height</c>, read a declaration out of a comment, took the first of two declarations where CSS
/// takes the last, and could not read a value carrying <c>!important</c>. Every test but the guard
/// failed against it. The lookup now parses the attribute the way the iframe's own inline style is
/// parsed (<c>DomBridgeUtils.ParseStyle</c>), so it reads the declaration that style applies and none
/// that style drops. It is still that one declaration rather than the frame's used content box: min/max
/// clamps, <c>box-sizing</c>, padding and border, and an author <c>!important</c> rule overriding the
/// inline value are not applied, and no test here depends on them.
/// </para>
/// <para>
/// <b>How the probe reads the viewport.</b> The frame's document carries two stylesheets gated by
/// <c>media</c>: one turns <c>#w</c> <c>inline-block</c> at a viewport at least 400px wide, the other
/// <c>#h</c> at one at least 400px tall. Every width below is either comfortably above that line
/// (450px, 500px, 999px) or below it (≤ 200px), so each answer names which declaration was read.
/// </para>
/// </summary>
public class FrameViewportInlineStyleTests
{
    private const string PageUrl = "https://example.test/frame-viewport";

    /// <summary>
    /// A standards-mode page holding one <c>srcdoc</c> frame with <paramref name="frameAttributes"/>. The
    /// frame's inner attributes are single-quoted so the double-quoted <c>srcdoc</c> survives, as in
    /// <see cref="FrameDocumentTests"/>.
    /// </summary>
    private static string PageHtml(string frameAttributes) =>
        "<!DOCTYPE html><html><head><title>t</title></head><body>" +
        "<iframe id=\"f\" " + frameAttributes + " srcdoc=\"<!DOCTYPE html><html><head>" +
        "<style media='(min-width: 400px)'>#w{display:inline-block}</style>" +
        "<style media='(min-height: 400px)'>#h{display:inline-block}</style>" +
        "</head><body><div id='w'></div><div id='h'></div></body></html>\"></iframe>" +
        "<div id=\"out\"></div>" +
        "</body></html>";

    /// <summary>Whether the frame's document matched the wide and the tall media query.</summary>
    private const string Probe = """
        (function () {
          var d = document.getElementById('f').contentDocument;
          return 'wide=' + (getComputedStyle(d.getElementById('w')).display === 'inline-block') +
                 ' tall=' + (getComputedStyle(d.getElementById('h')).display === 'inline-block');
        })()
        """;

    /// <summary>
    /// Runs <see cref="Probe"/> against a page whose frame carries <paramref name="frameAttributes"/>, and
    /// returns what it wrote to <c>#out</c>; a throw is written there too, as in
    /// <see cref="NodeRelationshipCanonicalTests"/>.
    /// </summary>
    private static string ViewportOf(string frameAttributes)
    {
        var html = new ScriptEngine().Execute(
            [
                "var probeResult;" +
                $"try {{ probeResult = String({Probe}); }} " +
                "catch (e) { probeResult = 'threw ' + (e && e.name) + ': ' + (e && e.message); }" +
                "document.getElementById('out').textContent = probeResult;",
            ],
            PageHtml(frameAttributes),
            PageUrl);

        Assert.NotNull(html);

        const string open = "<div id=\"out\">";
        var start = html!.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no #out div in serialized output: {html}");
        start += open.Length;
        var end = html.IndexOf("</div>", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"unterminated #out div in serialized output: {html}");
        return html[start..end];
    }

    [Fact]
    public void AWidthInsideAnotherPropertysNameDoesNotSizeTheFrame()
    {
        // What changed: the declaration is matched by its name, not by a substring of the attribute.
        // `max-width: 500px` sized the frame 500 wide ahead of `width: 200px`; `border-width: 2px` sized
        // it 2 wide ahead of `width: 450px`; `line-height: 450px` made it 450 tall ahead of
        // `height: 100px`. Chromium's frames are 200x100, 450x100 and 200x100.
        Assert.Equal(
            "maxWidth[wide=false tall=false] borderWidth[wide=true tall=false] lineHeight[wide=false tall=false]",
            "maxWidth[" + ViewportOf("style=\"max-width: 500px; width: 200px; height: 100px\"") + "]" +
            " borderWidth[" + ViewportOf("style=\"border-width: 2px; width: 450px; height: 100px\"") + "]" +
            " lineHeight[" + ViewportOf("style=\"line-height: 450px; width: 200px; height: 100px\"") + "]");
    }

    [Fact]
    public void TheLastImportantOrUncommentedWidthSizesTheFrame()
    {
        // What changed: CSS takes the last valid declaration of a property, `!important` does not make
        // a value unreadable, and a comment is not a declaration. The search took the first `width`
        // (100), read `450px !important` as no length at all (0), and read 999 out of the comment.
        // Chromium sizes all three frames by the declaration a stylesheet would apply: 450, 450, 200.
        Assert.Equal(
            "duplicate[wide=true tall=false] important[wide=true tall=false] comment[wide=false tall=false]",
            "duplicate[" + ViewportOf("style=\"width: 100px; width: 450px; height: 100px\"") + "]" +
            " important[" + ViewportOf("style=\"width: 450px !important; height: 100px\"") + "]" +
            " comment[" + ViewportOf("style=\"/* width: 999px; */ width: 200px; height: 100px\"") + "]");
    }

    [Fact]
    public void AStyleThatNamesNoWidthOrHeightFallsThroughToTheFramesAttributes()
    {
        // What changed: an inline style that declares neither `width` nor `height` no longer stands in
        // for them. The search found `width` inside `max-width` and returned 500x0 from the inline
        // branch, which skipped the frame's width/height attributes altogether. The attributes now
        // size it 200x100, as in Chromium, where a 500px maximum does not bind a 200px frame.
        Assert.Equal(
            "wide=false tall=false",
            ViewportOf("style=\"max-width: 500px\" width=\"200\" height=\"100\""));
    }

    [Fact]
    public void AUnitlessWidthIsDroppedAsTheFramesOwnStyleDropsIt()
    {
        // What changed: `width: 450` is a parse error outside quirks mode (CSS 2.1 §4.3.2), and the
        // bridge's inline-style parse already dropped it from the iframe's own style — the style the
        // renderer lays the frame out with and getComputedStyle reports. The viewport branch read 450
        // anyway, so the frame's document was 450 wide inside a frame that was not. Chromium drops it
        // in this standards-mode page and keeps the frame's default 300px width; the bridge has no such
        // default here, so all this asserts is that the frame is not 450 wide.
        //
        // Not Chromium in a quirks-mode page, where the unitless length is honoured and the frame is 450
        // wide — the width the substring search read in either mode. The iframe's own style drops the
        // value in that page too, because Broiler.CSS reads the unitless-length quirk from
        // CssDocumentMode.QuirksMode and the bridge sets only Layout.DocumentModeContext.CurrentQuirksMode
        // (DomBridge/HtmlParsing.cs, ParseHtml). Setting both there is reachable with the consumed
        // package — the property has a public setter — but it changes how the whole cascade parses a
        // quirks-mode page, so it is a separate change with its own tests, not part of this lookup.
        Assert.Equal(
            "wide=false tall=false",
            ViewportOf("style=\"width: 450; height: 100px\""));
    }

    [Fact]
    public void APlainPixelSizeStillSizesTheFrame()
    {
        // A guard for the common case, unchanged by the lookup: pixel widths and heights on either side
        // of the 400px line.
        Assert.Equal(
            "large[wide=true tall=true] small[wide=false tall=false]",
            "large[" + ViewportOf("style=\"width: 450px; height: 450px\"") + "]" +
            " small[" + ViewportOf("style=\"width: 200px; height: 100px\"") + "]");
    }
}
