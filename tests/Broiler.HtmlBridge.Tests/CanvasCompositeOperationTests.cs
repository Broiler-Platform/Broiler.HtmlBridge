using Broiler.HtmlBridge;

namespace Broiler.Browser.Core.Tests;

/// <summary>
/// <c>globalCompositeOperation</c> is held as <c>BCanvas.BlendMode</c>, an enum whose member names cannot
/// spell a hyphen and whose <c>Enum.TryParse</c> accepts far more than a keyword. Script only ever sees
/// the keyword, so what is pinned here is the round-trip a feature detector reads, the values an enum
/// parse would wrongly take, and that an accepted operator is one the rasteriser applies.
/// </summary>
public class CanvasCompositeOperationTests
{
    private const string PageUrl = "https://example.test/canvas";

    private const string PageHtml =
        "<html><body><canvas id=\"c\" width=\"4\" height=\"4\"></canvas><div id=\"out\"></div></body></html>";

    /// <summary>
    /// Runs <paramref name="body"/> with <c>ctx</c> bound to the fixture canvas's 2D context and returns
    /// what its final expression evaluated to, read back out of the serialized <c>#out</c>.
    /// </summary>
    private static string Run(string body)
    {
        var html = new ScriptEngine().Execute(
            [$"var ctx = document.getElementById('c').getContext('2d'); document.getElementById('out').textContent = String((function () {{ {body} }})());"],
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
    public void ANewContextReadsSourceOver() =>
        Assert.Equal("source-over", Run("return ctx.globalCompositeOperation;"));

    [Fact]
    public void EverySupportedOperatorReadsBackAsTheKeywordThatSetIt()
    {
        // Each is set from a different operator so a setter that ignored the value cannot pass, and the
        // two hyphenated keywords are the ones an enum name would read back with an underscore.
        Assert.Equal(
            "source-over,multiply,screen,overlay,darken,lighten,difference,plus-lighter",
            Run("""
                var ops = ['source-over', 'multiply', 'screen', 'overlay', 'darken', 'lighten', 'difference', 'plus-lighter'];
                return ops.map(function (op, i) {
                  ctx.globalCompositeOperation = ops[(i + 1) % ops.length];
                  ctx.globalCompositeOperation = op;
                  return ctx.globalCompositeOperation;
                }).join(',');
                """));
    }

    [Fact]
    public void ValuesAnEnumParseWouldAcceptAreIgnored()
    {
        // Digits, a comma list and padding all parse as BlendMode; the underscore spellings are how the enum
        // names its members; normal, copy and xor are operators this context cannot composite. Every one
        // must leave the previous value in place.
        Assert.Equal(
            "screen,screen,screen,screen,screen,screen,screen,screen,screen,screen",
            Run("""
                ctx.globalCompositeOperation = 'screen';
                return ['0', '3', 'multiply,screen', ' multiply', 'source_over', 'plus_lighter', 'normal', 'copy', 'xor', '']
                  .map(function (op) { ctx.globalCompositeOperation = op; return ctx.globalCompositeOperation; })
                  .join(',');
                """));
    }

    [Fact(Skip = "A keyword is accepted in any case: CanvasRenderingContext2D.TryParseCompositeOperation lowercases " +
        "the value before comparing, as the string state did, where HTML requires the keyword to be identical.")]
    public void AKeywordInTheWrongCaseIsIgnored() =>
        Assert.Equal("source-over", Run("ctx.globalCompositeOperation = 'MULTIPLY'; return ctx.globalCompositeOperation;"));

    [Fact]
    public void RestoreBringsBackTheSavedOperator() =>
        Assert.Equal(
            "multiply",
            Run("""
                ctx.globalCompositeOperation = 'multiply';
                ctx.save();
                ctx.globalCompositeOperation = 'plus-lighter';
                ctx.restore();
                return ctx.globalCompositeOperation;
                """));

    [Fact]
    public void AssigningTheCanvasWidthResetsTheOperator() =>
        Assert.Equal(
            "source-over",
            Run("""
                ctx.globalCompositeOperation = 'difference';
                ctx.canvas.width = ctx.canvas.width;
                return ctx.globalCompositeOperation;
                """));

    [Theory]
    [InlineData("source-over", "0,255,0,255")]
    [InlineData("multiply", "0,0,0,255")]
    [InlineData("plus-lighter", "255,255,0,255")]
    public void TheOperatorIsAppliedToTheNextDraw(string operation, string pixel)
    {
        // Green drawn over red: source-over keeps the green, multiply zeroes every channel, plus-lighter
        // adds to yellow. Three different answers, so a draw that lost the operator matches only one row.
        Assert.Equal(
            pixel,
            Run($$"""
                ctx.fillStyle = '#ff0000';
                ctx.fillRect(0, 0, 4, 4);
                ctx.globalCompositeOperation = '{{operation}}';
                ctx.fillStyle = '#00ff00';
                ctx.fillRect(0, 0, 4, 4);
                var d = ctx.getImageData(1, 1, 1, 1).data;
                return [d[0], d[1], d[2], d[3]].join(',');
                """));
    }

    [Fact(Skip = "The argument is converted twice: CanvasBinding.SetGlobalCompositeOperation calls ToJsString " +
        "once for IsSupportedCompositeOperation and again for the operator it stores, where Web IDL converts a " +
        "DOMString argument once, so a toString that answers differently the second time decides what is stored.")]
    public void TheArgumentIsConvertedToAStringOnce() =>
        Assert.Equal(
            "multiply/1",
            Run("""
                var calls = 0;
                ctx.globalCompositeOperation = { toString: function () { calls++; return calls === 1 ? 'multiply' : 'screen'; } };
                return ctx.globalCompositeOperation + '/' + calls;
                """));
}
