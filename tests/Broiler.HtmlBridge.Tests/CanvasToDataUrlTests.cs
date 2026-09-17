using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.HtmlBridge.Jseal;
using Broiler.Media;
using Broiler.Media.Image;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// <c>canvas.toDataURL(type, quality)</c>: which type is produced, and what quality reaches the encoder.
/// <para>
/// <b>This process registers no image codec catalog</b> — nothing in <c>src</c> or <c>tests</c> calls
/// <c>BImageCodecs.Use</c>, and <c>Broiler.Media.Image.Managed</c> is not in the closure — so every
/// <c>toDataURL</c> a page makes here answers <c>data:,</c>, and the first test pins exactly that. The
/// negotiation itself is pinned against catalogs built here and handed to
/// <see cref="CanvasBinding.ResolveEncodeFormat"/>. <b>Never call <c>BImageCodecs.Use</c> in this
/// suite:</b> it is process-global, the last registration wins and none can be undone, so it would race
/// the page test across test classes xunit runs in parallel.
/// </para>
/// <para>
/// <b>What changed.</b> The type used to be matched against a hard-coded list after trimming and
/// lowercasing: a listed type with no registered encoder (<c>image/gif</c>, <c>image/bmp</c>) threw inside
/// the encoder and answered <c>data:,</c> where HTML says <c>image/png</c>; a registered encoder the list
/// did not name (<c>image/webp</c>) was never used; and <c>image/jpg</c> and <c> image/jpeg</c> produced
/// JPEG where HTML and Chromium produce PNG. The quality was forwarded to every format, so a quality of 0
/// made even PNG answer <c>data:,</c>.
/// </para>
/// </summary>
public class CanvasToDataUrlTests
{
    private const string PageUrl = "https://example.test/canvas-data-url";

    private const string PageHtml =
        "<html><body><canvas id=\"c\" width=\"4\" height=\"4\"></canvas><div id=\"out\"></div></body></html>";

    private static string Run(string body)
    {
        var html = new ScriptEngine().Execute(
            [$"var c = document.getElementById('c'); document.getElementById('out').textContent = String((function () {{ {body} }})());"],
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
    public void EveryTypeIsTheEmptyDataUrlWithoutACodecCatalog() =>
        Assert.Equal(
            "data:,|data:,|data:,|data:,|data:,",
            Run("""
                var ctx = c.getContext('2d');
                ctx.fillStyle = '#f00';
                ctx.fillRect(0, 0, 4, 4);
                return [c.toDataURL(), c.toDataURL('image/png'), c.toDataURL('image/jpeg'),
                        c.toDataURL('image/webp'), c.toDataURL('image/bogus')].join('|');
                """));

    [Theory]
    [InlineData("image/png", ImageEncodeFormat.Png)]
    [InlineData("image/jpeg", ImageEncodeFormat.Jpeg)]
    [InlineData("IMAGE/JPEG", ImageEncodeFormat.Jpeg)]
    // No encoder registered for these three, so HTML's fallback applies.
    [InlineData("image/webp", ImageEncodeFormat.Png)]
    [InlineData("image/gif", ImageEncodeFormat.Png)] // the GIF codec below decodes only
    [InlineData("image/bmp", ImageEncodeFormat.Png)]
    // Not a supported type as HTML compares types: no alias, no trimming, ASCII case folding only.
    [InlineData("image/jpg", ImageEncodeFormat.Png)]
    [InlineData(" image/jpeg", ImageEncodeFormat.Png)]
    [InlineData("image/bogus", ImageEncodeFormat.Png)]
    [InlineData("ımage/jpeg", ImageEncodeFormat.Png)] // U+0131 DOTLESS I is not an ASCII 'i' in any case
    public void ResolveEncodeFormatPicksATypeOnlyWhenItsEncoderIsRegistered(string requested, ImageEncodeFormat expected)
    {
        var catalog = new MediaCodecCatalog(
        [
            new StubCodec("png", "image/png", MediaCodecCapabilities.Decode | MediaCodecCapabilities.Encode),
            new StubCodec("jpeg", "image/jpeg", MediaCodecCapabilities.Encode),
            new StubCodec("gif", "image/gif", MediaCodecCapabilities.Decode),
        ]);

        Assert.Equal((expected, expected.GetMimeType()), CanvasBinding.ResolveEncodeFormat(catalog, requested));
    }

    [Fact]
    public void ResolveEncodeFormatNegotiatesWebPWhenAnEncoderIsRegistered()
    {
        var catalog = new MediaCodecCatalog(
        [
            new StubCodec("png", "image/png", MediaCodecCapabilities.Encode),
            new StubCodec("webp", "image/webp", MediaCodecCapabilities.Encode),
        ]);

        Assert.Equal((ImageEncodeFormat.WebP, "image/webp"), CanvasBinding.ResolveEncodeFormat(catalog, "image/webp"));
    }

    [Fact]
    public void ResolveEncodeFormatFallsBackToPngEvenWithoutAPngEncoder()
    {
        // The fallback is what HTML names; with no PNG encoder either, the encode fails into "data:,".
        var catalog = new MediaCodecCatalog([new StubCodec("jpeg", "image/jpeg", MediaCodecCapabilities.Encode)]);

        Assert.Equal((ImageEncodeFormat.Png, "image/png"), CanvasBinding.ResolveEncodeFormat(catalog, "image/webp"));
        Assert.Equal((ImageEncodeFormat.Jpeg, "image/jpeg"), CanvasBinding.ResolveEncodeFormat(catalog, "image/jpeg"));
    }

    [Theory]
    // Lossless formats ignore the argument. A 0 used to reach ImageEncodeOptions, which rejects anything
    // outside 1..100, and made the whole call answer "data:,".
    [InlineData(ImageEncodeFormat.Png, 0.0, 100)]
    [InlineData(ImageEncodeFormat.Png, 0.5, 100)]
    [InlineData(ImageEncodeFormat.Jpeg, 0.0, 1)]
    [InlineData(ImageEncodeFormat.Jpeg, 0.004, 1)]
    [InlineData(ImageEncodeFormat.Jpeg, 0.5, 50)]
    [InlineData(ImageEncodeFormat.WebP, 1.0, 100)]
    // Out of range, or not a number at all (a type test, not a coercion): the default.
    [InlineData(ImageEncodeFormat.Jpeg, 1.5, 92)]
    [InlineData(ImageEncodeFormat.Jpeg, -0.1, 92)]
    [InlineData(ImageEncodeFormat.Jpeg, "0.5", 92)]
    [InlineData(ImageEncodeFormat.WebP, null, 92)]
    public void EncodeQualityOnlyReachesLossyFormats(ImageEncodeFormat format, object? quality, int expected)
    {
        JsValue value = quality switch
        {
            double number => JsValue.Number(number),
            string text => JsValue.String(text),
            _ => JsValue.Undefined,
        };

        Assert.Equal(expected, CanvasBinding.EncodeQuality(format, value));
    }

    /// <summary>
    /// An image codec that only describes itself: <c>FindEncoder</c> reads the descriptor's kind,
    /// capabilities and MIME types and nothing else, so nothing here decodes, encodes or probes.
    /// </summary>
    private sealed class StubCodec(string id, string mimeType, MediaCodecCapabilities capabilities)
        : ImageCodec(new MediaCodecDescriptor(
            new MediaCodecId(id), id, MediaKind.Image, capabilities, [new MediaFormatDescriptor(id, [mimeType])]))
    {
        public override ValueTask<MediaProbeResult> ProbeAsync(
            MediaProbeRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(MediaProbeResult.NoMatch(MediaKind.Image));

        protected override ImageSequence DecodeCore(ReadOnlySpan<byte> data, ImageDecodeOptions options) =>
            throw new NotSupportedException();
    }
}
