using System.Text;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The scripts, modules, stylesheets and frame documents the bridge reads from <c>data:</c> URLs
/// decode as a browser decodes them, through Broiler.Net's <c>DataUrl</c>, whose own tests run
/// web-platform-tests' vectors.
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
    /// Control, which passes before and after: <c>atob</c>, which shares the <c>data:</c> URL
    /// decoder's forgiving-base64, decodes and refuses as it did.
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

    /// <summary><paramref name="text"/>'s UTF-8 bytes in base64, without the padding an encoder adds.</summary>
    private static string Unpadded(string text)
    {
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
        Assert.EndsWith("=", base64);
        return base64.TrimEnd('=');
    }
}
