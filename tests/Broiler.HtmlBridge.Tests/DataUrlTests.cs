using System.Text;
using System.Text.Json;
using Broiler.HtmlBridge.Net;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// <c>data:</c> URLs decode as a browser decodes them: web-platform-tests' own vectors for the
/// <c>data:</c> URL processor and its forgiving-base64 bodies (copied under <c>wpt/</c>), and the
/// scripts, modules, stylesheets and frame documents the bridge reads from them.
/// </summary>
/// <remarks>
/// Both of the bridge's decoders gave a base64 body to <see cref="Convert.FromBase64String"/>, which
/// throws for a body without its <c>=</c> padding where a browser decodes it. They caught the
/// exception and answered nothing, so such a script never ran, such a module was refused as empty,
/// and such a stylesheet or frame document was empty. Any <c>base64</c> in the declared type, not
/// only the closing <c>;base64</c>, also marked the body as base64.
/// </remarks>
public sealed class DataUrlTests
{
    private const string PageUrl = "https://example.test/data-urls/page.html";

    private static readonly IReadOnlyList<Vector> Base64Vectors =
        LoadVectors("base64.json", static entry => Bytes(entry[1]) is { } body
            ? new Vector("data:;base64," + entry[0].GetString(), "text/plain", body)
            : new Vector("data:;base64," + entry[0].GetString(), null, null));

    private static readonly IReadOnlyList<Vector> DataUrlVectors =
        LoadVectors("data-urls.json", static entry => entry.Length > 2
            ? new Vector(entry[0].GetString()!, entry[1].GetString()!.Split(';')[0], Bytes(entry[2]))
            : new Vector(entry[0].GetString()!, null, null));

    /// <summary>
    /// The vectors the URL parser decides rather than the processor: this processor takes the URL as
    /// written, so their failures are the parser's (see <see cref="DataUrl"/>).
    /// </summary>
    private static readonly HashSet<string> ParserVectors = ["data://test:test/,X"];

    public static TheoryData<int> Base64VectorIndexes() => Indexes(Base64Vectors.Count);

    public static TheoryData<int> DataUrlVectorIndexes() => Indexes(DataUrlVectors.Count);

    [Theory]
    [MemberData(nameof(Base64VectorIndexes))]
    public void A_Base64_Body_Decodes_As_Wpt_Expects(int index) => AssertVector(Base64Vectors[index]);

    [Theory]
    [MemberData(nameof(DataUrlVectorIndexes))]
    public void A_Data_Url_Decodes_As_Wpt_Expects(int index)
    {
        var vector = DataUrlVectors[index];
        if (!ParserVectors.Contains(vector.Input))
            AssertVector(vector);
    }

    [Fact]
    public void The_Mime_Type_Is_The_Essence_Without_The_Base64_Marker()
    {
        Assert.True(DataUrl.TryParse("data:TEXT/JavaScript;base64,YQ", out var mimeType, out _));
        Assert.Equal("text/javascript", mimeType);

        Assert.True(DataUrl.TryParse("data: text/html ;charset=utf-8,X", out mimeType, out _));
        Assert.Equal("text/html", mimeType);

        // No type, or one that does not parse, is text/plain.
        Assert.True(DataUrl.TryParse("data:;charset=x;base64,WA", out mimeType, out _));
        Assert.Equal("text/plain", mimeType);

        Assert.True(DataUrl.TryParse("data:image;base64,WA", out mimeType, out _));
        Assert.Equal("text/plain", mimeType);
    }

    [Fact]
    public void A_Text_Body_Is_Utf8_Without_Its_Byte_Order_Mark()
    {
        Assert.Equal("é", DataUrl.Utf8Decode([0xEF, 0xBB, 0xBF, 0xC3, 0xA9]));
        Assert.Equal("﻿A", DataUrl.Utf8Decode([0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, (byte)'A']));
        Assert.Equal("�A", DataUrl.Utf8Decode([0xFF, (byte)'A']));
        Assert.Equal(string.Empty, DataUrl.Utf8Decode([]));
    }

    /// <summary>
    /// <see cref="ScriptExtractionService.DecodeDataUri"/>, which classic, inserted and module
    /// scripts all read their program through: a body without its padding decodes, a type that
    /// only mentions base64 carries its body as written, and a URL that does not decode is empty.
    /// </summary>
    [Theory]
    [InlineData("data:text/javascript;base64,YWxlcnQoMSk", "alert(1)")]
    [InlineData("data:text/javascript;base64,YWxlcnQoMSk=", "alert(1)")]
    [InlineData("data:text/javascript;base64,YWxlcnQo%0A%20MSk%3D", "alert(1)")]
    [InlineData("data:text/javascript;x=base64,YQ", "YQ")]
    [InlineData("data:text/javascript;base64,A", "")]
    [InlineData("data:text/javascript", "")]
    [InlineData("javascript:alert(1)", "")]
    public void A_Script_Data_Url_Decodes_As_A_Browser_Decodes_It(string url, string program) =>
        Assert.Equal(program, ScriptExtractionService.DecodeDataUri(url));

    /// <summary>
    /// <c>&lt;script src="data:text/javascript;base64,…"&gt;</c> without its padding runs. It was
    /// extracted as no script at all.
    /// </summary>
    [Fact]
    public void A_Script_With_An_Unpadded_Base64_Source_Runs()
    {
        var program = Unpadded("document.getElementById('out').textContent = 'déjà ran';");
        var html =
            "<!DOCTYPE html><html><body><div id=\"out\">not run</div>" +
            $"<script src=\"data:text/javascript;base64,{program}\"></script>" +
            "</body></html>";

        var extraction = ScriptExtractionService.ExtractAll(html, PageUrl);
        var rendered = new ScriptEngine().Execute(
            extraction.Scripts, extraction.DeferredScripts, html, PageUrl, extraction.ModuleRoots);

        Assert.NotNull(rendered);
        Assert.Equal("déjà ran", PageProbe.OutOf(rendered!, decode: true));
    }

    /// <summary>
    /// A script the page inserts with an unpadded base64 <c>data:</c> source runs. It fired
    /// <c>error</c> instead, as an empty load.
    /// </summary>
    [Fact]
    public void An_Inserted_Script_With_An_Unpadded_Base64_Source_Runs()
    {
        var program = Unpadded("document.getElementById('out').textContent = 'inserted ran!';");
        var rendered = PageProbe.Render(
            [
                "var s = document.createElement('script');" +
                "s.onerror = function () { document.getElementById('out').textContent = 'error'; };" +
                $"s.src = 'data:text/javascript;base64,{program}';" +
                "document.body.appendChild(s);",
            ],
            "<html><body><div id=\"out\">not run</div></body></html>",
            PageUrl);

        Assert.Equal("inserted ran!", PageProbe.OutOf(rendered));
    }

    /// <summary>
    /// A module imports an unpadded base64 <c>data:</c> module. It was refused as an empty module.
    /// </summary>
    [Fact]
    public void A_Module_Imports_An_Unpadded_Base64_Module()
    {
        var module = Unpadded("export const word = 'imported!';");
        var html = $$"""
            <!DOCTYPE html><html><body>
            <div id="out">unset</div>
            <script type="module">
            import { word } from 'data:text/javascript;base64,{{module}}';
            document.getElementById('out').textContent = word;
            </script>
            </body></html>
            """;

        var extraction = ScriptExtractionService.ExtractAll(html, PageUrl);
        var rendered = new ScriptEngine().Execute([], [], html, PageUrl, extraction.ModuleRoots);

        Assert.NotNull(rendered);
        Assert.Equal("imported!", PageProbe.OutOf(rendered!));
    }

    /// <summary>
    /// <c>&lt;link rel="stylesheet" href="data:text/css;base64,…"&gt;</c> without its padding has
    /// its rule, and the rule applies. The sheet was empty.
    /// </summary>
    [Fact]
    public void A_Stylesheet_With_An_Unpadded_Base64_Source_Applies()
    {
        var css = Unpadded("#box { color: rgb(0, 128, 0); }");
        var html =
            "<!DOCTYPE html><html><head>" +
            $"<link rel=\"stylesheet\" href=\"data:text/css;base64,{css}\">" +
            "</head><body><div id=\"box\"></div><div id=\"out\"></div></body></html>";

        Assert.Equal(
            "1 rgb(0, 128, 0)",
            PageProbe.RunAgainst(html, PageUrl,
                "document.styleSheets[0].cssRules.length + ' ' + getComputedStyle(document.getElementById('box')).color"));
    }

    /// <summary>
    /// <c>&lt;iframe src="data:text/html;base64,…"&gt;</c> without its padding has its document,
    /// as UTF-8. The frame's document was empty.
    /// </summary>
    [Fact]
    public void A_Frame_With_An_Unpadded_Base64_Document_Has_It()
    {
        var document = Unpadded("<p id='in'>déjà vu!</p>");
        var html =
            $"<!DOCTYPE html><html><body><iframe id=\"f\" src=\"data:text/html;base64,{document}\"></iframe>" +
            "<div id=\"out\"></div></body></html>";

        Assert.Equal(
            "déjà vu!",
            PageProbe.RunAgainst(html, PageUrl,
                "(function () {" +
                "  var d = document.getElementById('f').contentDocument;" +
                "  var p = d && d.getElementById('in');" +
                "  return p ? p.textContent : 'no-frame';" +
                "})()",
                decode: true));
    }

    /// <summary>
    /// Control, which passes before and after: <c>atob</c>, which now shares the
    /// <c>data:</c> URL decoder's forgiving-base64, still decodes and refuses as it did.
    /// </summary>
    [Fact]
    public void Control_Atob_Decodes_By_Forgiving_Base64()
    {
        Assert.Equal(
            "a|a|InvalidCharacterError",
            PageProbe.RunAgainst(
                "<html><body><div id=\"out\"></div></body></html>",
                PageUrl,
                "[atob('YQ'), atob(' Y Q = = '), (function () { try { atob('YQ='); return 'decoded'; } catch (e) { return e.name; } })()].join('|')"));
    }

    /// <summary>
    /// A WPT vector: the URL, and the essence of its MIME type and its body when it decodes, both
    /// <see langword="null"/> when it does not.
    /// </summary>
    private sealed record Vector(string Input, string? Essence, byte[]? Body);

    private static void AssertVector(Vector vector)
    {
        var decoded = DataUrl.TryParse(vector.Input, out var mimeType, out var body);
        var shown = JsonSerializer.Serialize(vector.Input);
        if (vector.Body is null)
        {
            Assert.False(decoded, $"{shown} should not decode");
            return;
        }

        Assert.True(decoded, $"{shown} should decode");
        Assert.Equal(vector.Essence, mimeType);
        Assert.Equal(vector.Body, body);
    }

    /// <summary><paramref name="text"/>'s UTF-8 bytes in base64, without the padding an encoder adds.</summary>
    private static string Unpadded(string text)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        Assert.EndsWith("=", base64);
        return base64.TrimEnd('=');
    }

    private static IReadOnlyList<Vector> LoadVectors(string file, Func<JsonElement[], Vector> select)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "wpt", "fetch", "data-urls", "resources", file);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return [.. document.RootElement.EnumerateArray()
            .Where(static entry => entry.ValueKind == JsonValueKind.Array)
            .Select(entry => select([.. entry.EnumerateArray()]))];
    }

    private static byte[]? Bytes(JsonElement expected) =>
        expected.ValueKind == JsonValueKind.Null
            ? null
            : [.. expected.EnumerateArray().Select(static b => (byte)b.GetInt32())];

    private static TheoryData<int> Indexes(int count)
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < count; i++)
            data.Add(i);
        return data;
    }
}
