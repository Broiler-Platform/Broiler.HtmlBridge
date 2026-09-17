using Broiler.HtmlBridge;
using Xunit;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Tests verifying that the HTML User Agent stylesheet property defaults
/// (from Broiler.CSS.Dom.CssUserAgentDefaults.PropertyValues) are correctly
/// applied by the bridge and surfaced through getComputedStyle.
/// </summary>
public class UserAgentPropertyDefaultsTests
{
    private static string RunScript(string pageHtml, string script)
    {
        var html = new ScriptEngine().Execute(
            [$"document.getElementById('out').textContent = String({script});"],
            pageHtml,
            "https://example.test/ua-defaults");
        Assert.NotNull(html);
        var match = System.Text.RegularExpressions.Regex.Match(html, @"<div id=""out"">(.*?)</div>");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    [Theory]
    [InlineData("pre", "monospace", "pre")]
    [InlineData("xmp", "monospace", "pre")]
    [InlineData("listing", "monospace", "pre")]
    public void GetComputedStyle_ReflectsPreformattedDefaults(string tag, string expectedFont, string expectedWhiteSpace)
    {
        var page = $"<!DOCTYPE html><html><body><{tag} id=\"target\">test</{tag}><div id=\"out\"></div></body></html>";
        var font = RunScript(page, "getComputedStyle(document.getElementById('target')).fontFamily");
        var ws = RunScript(page, "getComputedStyle(document.getElementById('target')).whiteSpace");

        Assert.Equal(expectedFont, font);
        Assert.Equal(expectedWhiteSpace, ws);
    }

    [Fact]
    public void GetComputedStyle_ReflectsTextareaDefaults()
    {
        var page = "<!DOCTYPE html><html><body><textarea id=\"target\">test</textarea><div id=\"out\"></div></body></html>";
        var ws = RunScript(page, "getComputedStyle(document.getElementById('target')).whiteSpace");

        Assert.Equal("pre-wrap", ws);
    }

    [Fact]
    public void GetComputedStyle_AuthorStyleOverridesDefaults()
    {
        var page = "<!DOCTYPE html><html><head><style>pre { font-family: sans-serif; white-space: normal; }</style></head><body><pre id=\"target\">test</pre><div id=\"out\"></div></body></html>";
        var font = RunScript(page, "getComputedStyle(document.getElementById('target')).fontFamily");
        var ws = RunScript(page, "getComputedStyle(document.getElementById('target')).whiteSpace");

        Assert.Equal("sans-serif", font);
        Assert.Equal("normal", ws);
    }
}
