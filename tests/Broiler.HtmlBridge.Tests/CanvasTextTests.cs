using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The canvas 2D <c>font</c> attribute, <c>measureText</c> and <c>textAlign</c> placement, asserted from
/// page script.
/// <para>
/// <b>The font is a CSS <c>font</c> shorthand, and the setter is where it is parsed.</b> It used to be
/// stored as any string and re-scanned on every draw by a scanner that read the first token ending in
/// <c>px</c>, so <c>12px/1.5 Arial</c> drew at 10px in a family called <c>/1.5 Arial</c>, <c>2em</c> drew
/// at 10px, and <c>bogus</c> read back as <c>bogus</c>. HTML ignores an assignment that does not parse as
/// a CSS font value; the context now expands the shorthand through Broiler.CSS.Dom's
/// <c>CssStyleEngine.ExpandShorthands</c> once, at set time, and keeps the previous value when that yields
/// no size or no family. The getter still returns the string as assigned where HTML serializes it, so the
/// tests that only ask whether a value was accepted compare against the previous value instead.
/// </para>
/// <para>
/// <b>Measuring and drawing share one advance here.</b> <c>measureText</c> answered a fixed 0.62em per
/// UTF-16 unit while the glyphs advanced by the face's real widths, so right-, end- and center-aligned
/// text missed its anchor and a wide run was clipped by a scratch bitmap sized from the estimate. It now
/// measures through Broiler.Graphics' <c>BTextMeasurer</c>, whose built-in provider resolves the face the
/// renderer draws with. That is the provider in this test process; a host that registers its own — the
/// Windows browser registers DirectWrite's — measures in a face and with shaping the renderer does not
/// draw with, and there these alignments hold only approximately. The pixel assertions below only detect
/// a regression on a machine with a system font: on a font-less host both sides fall back to the block
/// font and agree either way.
/// </para>
/// <para>
/// Long runs are built with <c>new Array(n + 1).join(c)</c> rather than <c>String.prototype.repeat</c> so
/// the same script runs on both engine configurations.
/// </para>
/// </summary>
public class CanvasTextTests
{
    private const string PageUrl = "https://example.test/canvas-text";

    private const string PageHtml =
        "<html><body><canvas id=\"c\" width=\"400\" height=\"40\"></canvas><div id=\"out\"></div></body></html>";

    /// <summary>
    /// Runs <paramref name="body"/> with <c>ctx</c> bound to the fixture canvas's 2D context and
    /// <c>ink()</c> answering <c>[left, right]</c>, the first and last columns holding a pixel with alpha
    /// above 64 (<c>[-1, -1]</c> for a blank canvas), and returns what the body's final expression
    /// evaluated to, read back out of the serialized <c>#out</c>.
    /// </summary>
    private static string Run(string body)
    {
        const string prelude =
            "var ctx = document.getElementById('c').getContext('2d');" +
            "function ink() { var d = ctx.getImageData(0, 0, 400, 40).data, l = -1, r = -1;" +
            " for (var x = 0; x < 400; x++) { for (var y = 0; y < 40; y++) {" +
            " if (d[(y * 400 + x) * 4 + 3] > 64) { if (l < 0) { l = x; } r = x; break; } } }" +
            " return [l, r]; }";

        var html = new ScriptEngine().Execute(
            [$"{prelude} document.getElementById('out').textContent = String((function () {{ {body} }})());"],
            PageHtml,
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
    public void AnAssignmentThatIsNotACssFontIsIgnored()
    {
        // This changed. The setter used to store any string, so every one of these read back as assigned.
        // HTML ignores a value that does not parse as a CSS font (no size, no family, a negative size) and
        // the CSS-wide keywords, leaving the previous value in place.
        Assert.Equal(
            "20px serif|20px serif|20px serif|20px serif|20px serif|20px serif|20px serif",
            Run("""
                ctx.font = '20px serif';
                return ['bogus', '20px', 'serif', '', 'inherit', '-5px serif', 'bold'].map(function (v) {
                  ctx.font = v;
                  var r = ctx.font;
                  ctx.font = '20px serif';
                  return r;
                }).join('|');
                """));
    }

    [Fact]
    public void ValidFontsAreAccepted()
    {
        // Guards the parser against rejecting what CSS accepts: a slash with a unitless line-height, a
        // quoted family, a numeric weight, an upper-case unit, a keyword size and calc(). Acceptance is
        // read as "the previous font was replaced", not as a verbatim read-back, which HTML's serializing
        // getter would not give (see TheFontSerializesWithoutLineHeight).
        Assert.Equal(
            "true,true,true,true,true,true",
            Run("""
                return ['italic bold 12px/30px Georgia, serif', 'bold 2em "Times New Roman", serif',
                        'small-caps 900 1.5em/2 monospace', '12PX sans-serif', 'large sans-serif',
                        'calc(10px + 2pt) serif'].map(function (v) {
                  ctx.font = '10px sans-serif';
                  ctx.font = v;
                  return ctx.font !== '10px sans-serif';
                }).join(',');
                """));
    }

    [Fact]
    public void AUnitlessSizeIsIgnoredAndANumberBeforeTheSizeIsAWeight()
    {
        // This changed twice over. A unitless font-size other than 0 is invalid outside quirks mode, and
        // HTML parses the canvas font as it would a declaration in a standards-mode sheet, so '12 serif' is
        // ignored, as Chromium ignores it; it used to be stored and drawn at 10px sans-serif. And CSS Fonts 4
        // allows any weight in [1, 1000], so '550 12px serif' is weight 550 at 12px in serif, which draws in
        // the regular face (bold starts at 700): Broiler.CSS.Dom's expansion knows only the hundreds as
        // weights and reads 550 as the size, which drew at 550px in a family called '12px serif' until the
        // resolver read such a number as the weight. '1200 12px serif' is no weight and is ignored.
        Assert.Equal(
            "20px serif,true,true,20px serif",
            Run("""
                function w(f) { ctx.font = f; return ctx.measureText('HHHH').width; }
                ctx.font = '20px serif';
                ctx.font = '12 serif';
                var unitless = ctx.font;
                var twelve = w('12px serif');
                var weighted = w('550 12px serif');
                var accepted = ctx.font === '550 12px serif';
                ctx.font = '20px serif';
                ctx.font = '1200 12px serif';
                return [unitless, weighted === twelve, accepted, ctx.font].join(',');
                """));
    }

    [Fact]
    public void ASystemFontIsAccepted()
    {
        // Kept. The setter stored 'caption' before, and Chromium accepts every CSS system-font keyword; the
        // expansion yields no size or family for one, so the resolver accepts the keywords by name. No
        // consumed API says what face and size a system font is, so it draws in the 10px sans-serif default.
        Assert.Equal(
            "true,true,true",
            Run("""
                return ['caption', 'status-bar', 'MENU'].map(function (v) {
                  ctx.font = '20px serif';
                  ctx.font = v;
                  return ctx.font !== '20px serif';
                }).join(',');
                """));
    }

    [Fact(Skip = "CssStyleEngine.ExpandShorthands (Broiler.CSS.Dom 0.1.0-preview.1) passes over tokens before the " +
        "size it does not recognise and takes everything after the size as the family unchecked, so CanvasFont.TryResolve " +
        "accepts these; Chromium ignores each one.")]
    public void JunkAroundTheSizeIsIgnored() =>
        Assert.Equal(
            "20px serif|20px serif|20px serif|20px serif",
            Run("""
                return ['bogus 20px serif', 'unset 20px serif', '20px serif 30px', '20px , serif'].map(function (v) {
                  ctx.font = '20px serif';
                  ctx.font = v;
                  return ctx.font;
                }).join('|');
                """));

    [Fact(Skip = "The getter returns the assigned string (CanvasRenderingContext2D.Font); HTML and Chromium " +
        "return the serialized font, without the line-height, and no consumed Broiler.CSS API serializes a font " +
        "shorthand.")]
    public void TheFontSerializesWithoutLineHeight() =>
        Assert.Equal(
            "italic bold 12px Georgia, serif",
            Run("ctx.font = 'italic bold 12px/30px Georgia, serif'; return ctx.font;"));

    [Fact]
    public void ALineHeightDoesNotChangeTheSize()
    {
        // This changed. The scanner only read a token ending in 'px', so '12px/1.5' fell back to 10px.
        Assert.Equal(
            "true,true",
            Run("""
                ctx.font = '12px sans-serif';
                var a = ctx.measureText('HHHH').width;
                ctx.font = '12px/1.5 sans-serif';
                var b = ctx.measureText('HHHH').width;
                return (a === b) + ',' + (a > 0);
                """));
    }

    [Fact]
    public void RelativeSizesResolveAgainstTheTenPixelDefault()
    {
        // This changed: em and % used to be ignored (10px). A detached canvas has no element font to
        // resolve against, so HTML and Chromium both use the 10px sans-serif default. The third entry
        // catches a unit match that forgot to lowercase, which would resolve '20PX' to 0.
        Assert.Equal(
            "true,true,true",
            Run("""
                var x = document.createElement('canvas').getContext('2d');
                function w(f) { x.font = f; return x.measureText('HHHH').width; }
                return [w('2em sans-serif') === w('20px sans-serif'),
                        w('150% sans-serif') === w('15px sans-serif'),
                        w('20PX sans-serif') === w('20px sans-serif')].join(',');
                """));
    }

    [Fact]
    public void AKeywordSizeKeepsTheDefaultSize()
    {
        // Preserved. No consumed API sizes an absolute-size keyword, so it keeps the 10px default rather
        // than the 0 a length parser answers for a keyword, which would make the text vanish. The first
        // entry asks only that the value was accepted.
        Assert.Equal(
            "true,true,true",
            Run("""
                function w(f) { ctx.font = f; return ctx.measureText('HHHH').width; }
                var d = w('10px sans-serif');
                var k = w('large sans-serif');
                return (ctx.font !== '10px sans-serif') + ',' + (k === d) + ',' + (k > 0);
                """));
    }

    [Fact(Skip = "Absolute-size keywords resolve to the 10px default in CanvasFont.TryResolve: " +
        "no consumed Broiler.CSS or Broiler.Layout public API maps them to pixels. Chromium uses 18px for 'large'.")]
    public void AKeywordSizeFollowsTheCssTable() =>
        Assert.Equal(
            "true",
            Run("""
                function w(f) { ctx.font = f; return ctx.measureText('HHHH').width; }
                return String(w('large sans-serif') === w('18px sans-serif'));
                """));

    [Fact(Skip = "Relative sizes resolve against the 10px default in CanvasFont.TryResolve " +
        "because ICanvasHost exposes no computed style for the canvas element; HTML and Chromium resolve them " +
        "against a connected canvas's computed font-size (16px here).")]
    public void RelativeSizesOnAConnectedCanvasFollowItsComputedFont() =>
        Assert.Equal(
            "true",
            Run("""
                function w(f) { ctx.font = f; return ctx.measureText('HHHH').width; }
                return String(w('2em sans-serif') === w('32px sans-serif'));
                """));

    [Fact(Skip = "CssStyleEngine.ExpandShorthands (Broiler.CSS.Dom 0.1.0-preview.1) classifies the size with " +
        "CssLengthParser.ParseToPixels and no viewport, so a vw/vh size, or a calc() holding a percentage, yields no " +
        "font-size and CanvasFont.TryResolve rejects the assignment; Chromium accepts both. The setter stored " +
        "'5vw serif' before and drew it at 10px sans-serif.")]
    public void AViewportRelativeOrPercentageCalcSizeIsAccepted() =>
        Assert.Equal(
            "true,true",
            Run("""
                return ['5vw serif', 'calc(50% + 2px) serif'].map(function (v) {
                  ctx.font = '20px serif';
                  ctx.font = v;
                  return ctx.font !== '20px serif';
                }).join(',');
                """));

    [Fact(Skip = "CanvasFont.TryResolve draws in the first family of the list only; Broiler.Graphics' BFontStyle " +
        "takes one family, and no consumed API falls back along a list. Chromium falls back to serif here.")]
    public void AnUnavailableFamilyFallsBackAlongTheList() =>
        Assert.Equal(
            "true",
            Run("""
                function w(f) { ctx.font = f; return ctx.measureText('HHHHiiii').width; }
                return String(w('20px NoSuchFamilyForBroilerTests, serif') === w('20px serif'));
                """));

    [Fact]
    public void MeasuringPreparesWhitespaceAsSpaces()
    {
        // HTML's text preparation replaces ASCII whitespace with spaces before measuring or drawing.
        // BTextMeasurer skips \r and \n (the renderer resets its pen there instead), so without that step
        // the first and third entries would be false.
        Assert.Equal(
            "true,true,true",
            Run("""
                ctx.font = '20px sans-serif';
                var s = ctx.measureText('a b').width;
                return [ctx.measureText('a\nb').width === s,
                        ctx.measureText('a\tb').width === s,
                        ctx.measureText('a\r\nb').width === ctx.measureText('a  b').width].join(',');
                """));
    }

    [Fact]
    public void DrawingPreparesWhitespaceAsSpaces()
    {
        // This changed. The draw path gets the same preparation as measuring: the renderer moves its pen
        // back to the start of the run at a line feed, so 'H\nH' drew the second H over the first and its
        // ink ended one glyph in. It now ends where 'H H' is measured to, within the last glyph's side
        // bearing — which a draw path that skipped the preparation fails even though measureText passes.
        Assert.Equal(
            "true",
            Run("""
                ctx.font = '20px sans-serif';
                ctx.fillText('H\nH', 0, 30);
                var e = ink();
                var w = ctx.measureText('H H').width;
                return String(e[1] <= w + 1 && e[1] >= w - 6);
                """));
    }

    [Fact]
    public void TheWeightSelectsTheFace()
    {
        // This changed. The scanner only looked for the word 'bold', so '700' drew in the regular face; the
        // resolver now reads the weight, and Graphics draws the bold face from 700 up, keyword or number,
        // hundred or not. 'rrrr' advances differently in the bold and regular faces of the common sans-serif
        // fonts. That half only detects a regression on a host whose system font has a bold face, and a
        // font-less host, where every glyph is the same block and 'iiii' measures as 'WWWW', passes it
        // either way.
        Assert.Equal(
            "true,true,true",
            Run("""
                function w(f, t) { ctx.font = f; return ctx.measureText(t || 'rrrr').width; }
                var regular = w('20px sans-serif'), bold = w('bold 20px sans-serif');
                var proportional = w('20px sans-serif', 'iiii') !== w('20px sans-serif', 'WWWW');
                return [bold === w('700 20px sans-serif') && bold === w('750 20px sans-serif'),
                        regular === w('400 20px sans-serif') && regular === w('550 20px sans-serif'),
                        !proportional || bold !== regular].join(',');
                """));
    }

    [Fact]
    public void TheResolvedFontIsSavedRestoredAndReset()
    {
        // The font is resolved at set time now, so save/restore and the width reset must carry the resolved
        // font along with the string; this fails if either forgets it.
        Assert.Equal(
            "true,true",
            Run("""
                function w() { return ctx.measureText('HH').width; }
                ctx.font = '40px sans-serif';
                var big = w();
                ctx.save();
                ctx.font = '10px sans-serif';
                var small = w();
                ctx.restore();
                var restored = (w() === big) && ctx.font === '40px sans-serif';
                ctx.canvas.width = ctx.canvas.width;
                var reset = (w() === small) && ctx.font === '10px sans-serif';
                return restored + ',' + reset;
                """));
    }

    [Fact]
    public void MeasureTextIsAdditiveAndScalesWithTheSize()
    {
        // The tolerances cover the measurer rounding each answer to two decimals.
        Assert.Equal(
            "true,true,true",
            Run("""
                function w(f, t) { ctx.font = f; return ctx.measureText(t).width; }
                var f = '20px sans-serif';
                return [w(f, '') === 0,
                        Math.abs(w(f, 'HiW') - (w(f, 'H') + w(f, 'i') + w(f, 'W'))) <= 0.03,
                        Math.abs(w(f, 'HiW') - 2 * w('10px sans-serif', 'HiW')) <= 0.05].join(',');
                """));
    }

    [Theory]
    [InlineData("right")]
    [InlineData("end")]
    public void RightAndEndAlignedTextEndsAtTheAnchor(string align)
    {
        // This changed. The shift used the 0.62em estimate while the glyphs advanced by the real face, so
        // right-aligned 'H's (wider than 0.62em in Arial or Segoe UI) overshot the anchor and were clipped by a scratch
        // bitmap sized from the same estimate. The ink now ends within the last glyph's side bearing.
        Assert.Equal(
            "true",
            Run($$"""
                ctx.font = '20px sans-serif';
                ctx.fillStyle = '#000';
                ctx.textAlign = '{{align}}';
                ctx.fillText(new Array(11).join('H'), 290, 30);
                var e = ink();
                return String(e[1] <= 291 && e[1] >= 284);
                """));
    }

    [Fact]
    public void CenteredTextIsCenteredOnTheAnchor()
    {
        // This changed, for the same reason as right alignment: half the estimate was not half the run.
        Assert.Equal(
            "true",
            Run("""
                ctx.font = '20px sans-serif';
                ctx.textAlign = 'center';
                ctx.fillText(new Array(11).join('H'), 200, 30);
                var e = ink();
                return String(Math.abs((e[0] + e[1]) / 2 - 200) <= 1.5);
                """));
    }

    [Fact]
    public void ALongRunIsNotClippedAndMatchesItsMeasuredWidth()
    {
        // This changed. The scratch bitmap was sized from the estimate (12 x 12.4px plus a margin) and cut
        // off the real run. Measured after drawing, so a font resolver registered part-way through the
        // script cannot put the measurement and the draw on different faces.
        Assert.Equal(
            "true",
            Run("""
                ctx.font = '20px sans-serif';
                ctx.fillText(new Array(13).join('W'), 0, 30);
                var e = ink();
                var w = ctx.measureText(new Array(13).join('W')).width;
                return String(e[1] <= w + 1 && e[1] >= w - 6);
                """));
    }
}
