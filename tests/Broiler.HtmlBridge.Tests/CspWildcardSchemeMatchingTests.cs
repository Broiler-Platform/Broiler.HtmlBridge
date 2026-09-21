using System;
using System.Collections.Generic;
using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Internal.Scripting;
using Broiler.HtmlBridge.Scripting;
using Xunit;
using TextRegex = System.Text.RegularExpressions.Regex;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Verifies Content Security Policy wildcard (<c>*</c>) source-expression matching per CSP3 §6.7.2.8
/// ("Does url match expression in origin with redirect count?").
/// <para>
/// A bare <c>*</c> matches HTTP(S) URLs, WebSocket URLs on HTTP(S) pages, or URLs whose scheme equals
/// the protected resource's scheme and is not a local scheme. Local schemes (<c>data:</c>, <c>blob:</c>,
/// <c>filesystem:</c>, <c>javascript:</c>, <c>about:</c>) and cross-scheme non-network loads (such as
/// a <c>file:</c> URL on an <c>http(s)</c> page) require an explicit scheme or host source and are not
/// admitted by <c>*</c>.
/// </para>
/// <para>
/// Every prohibition test here is paired with an admitting control test.
/// </para>
/// </summary>
public class CspWildcardSchemeMatchingTests
{
    private const string HttpsPageUrl = "https://example.test/index.html";
    private const string FilePageUrl = "file:///app/index.html";

    private const string DataCssRule = "#data-target { color: rgb(0, 128, 0); }";
    private const string OwnCssRule = "#own { color: rgb(0, 0, 255); }";

    private static string Page(string head, string body = "") =>
        $"<!DOCTYPE html><html><head>{head}</head><body>{body}</body></html>";

    private static string ExtractStyleText(string html, string id = "s")
    {
        var match = TextRegex.Match(html, $"<style[^>]*\\bid=\"{id}\"[^>]*>([\\s\\S]*?)</style>");
        Assert.True(match.Success, $"no <style id=\"{id}\"> in projection: {html}");
        return match.Groups[1].Value;
    }

    // =========================================================================
    //  1. <link rel="stylesheet"> with data: URL
    // =========================================================================

    [Fact]
    public void LinkStylesheet_DataUrl_RefusedUnderWildcardWithoutDataSource()
    {
        // Under style-src * 'unsafe-inline', data: stylesheet is NOT admitted by *
        var csp = CspFixture.CspOf("style-src * 'unsafe-inline'");
        var dataUrl = CspFixture.DataUrl(DataCssRule);

        Assert.False(csp.AllowsExternalStyle(dataUrl, HttpsPageUrl));

        var html = Page(
            CspFixture.Meta("style-src * 'unsafe-inline'") +
            $"<link rel=\"stylesheet\" id=\"l\" href=\"{dataUrl}\">",
            "<div id=\"data-target\">text</div><div id=\"out\"></div>");

        var engine = new ScriptEngine();
        var resultHtml = engine.Execute(
            ["document.getElementById('out').textContent = document.styleSheets[0].cssRules.length;"],
            html,
            HttpsPageUrl);

        Assert.NotNull(resultHtml);
        var match = TextRegex.Match(resultHtml!, @"<div id=""out"">(.*?)</div>");
        Assert.True(match.Success);
        Assert.Equal("0", match.Groups[1].Value);
    }

    [Fact]
    public void LinkStylesheet_DataUrl_AdmittedUnderWildcardWithDataSource()
    {
        // Control: adding data: to style-src * admits the data: stylesheet
        var csp = CspFixture.CspOf("style-src * 'unsafe-inline' data:");
        var dataUrl = CspFixture.DataUrl(DataCssRule);

        Assert.True(csp.AllowsExternalStyle(dataUrl, HttpsPageUrl));

        var html = Page(
            CspFixture.Meta("style-src * 'unsafe-inline' data:") +
            $"<link rel=\"stylesheet\" id=\"l\" href=\"{dataUrl}\">",
            "<div id=\"data-target\">text</div><div id=\"out\"></div>");

        var engine = new ScriptEngine();
        var resultHtml = engine.Execute(
            ["document.getElementById('out').textContent = document.styleSheets[0].cssRules.length + ' ' + window.getComputedStyle(document.getElementById('data-target')).color;"],
            html,
            HttpsPageUrl);

        Assert.NotNull(resultHtml);
        var match = TextRegex.Match(resultHtml!, @"<div id=""out"">(.*?)</div>");
        Assert.True(match.Success);
        Assert.Equal("1 rgb(0, 128, 0)", match.Groups[1].Value);
    }

    // =========================================================================
    //  2. @import with data: URL
    // =========================================================================

    [Fact]
    public void ImportStylesheet_DataUrl_RefusedUnderWildcardWithoutDataSource()
    {
        // Under style-src * 'unsafe-inline', @import url(data:...) is blocked; own rule survives
        var csp = CspFixture.CspOf("style-src * 'unsafe-inline'");
        var dataUrl = CspFixture.DataUrl(DataCssRule);

        Assert.False(csp.AllowsExternalStyle(dataUrl, HttpsPageUrl));

        var importCss = $"@import url({dataUrl}); {OwnCssRule}";
        var html = Page(
            CspFixture.Meta("style-src * 'unsafe-inline'") +
            $"<style id=\"s\">{importCss}</style>",
            "<p>text</p>");

        var engine = new ScriptEngine();
        var resultHtml = engine.Execute(["1;"], html, HttpsPageUrl);
        Assert.NotNull(resultHtml);

        var style = ExtractStyleText(resultHtml!);
        Assert.DoesNotContain(DataCssRule, style);
        Assert.Contains(OwnCssRule, style);
    }

    [Fact]
    public void ImportStylesheet_DataUrl_AdmittedUnderWildcardWithDataSource()
    {
        // Control: adding data: admits @import url(data:...)
        var csp = CspFixture.CspOf("style-src * 'unsafe-inline' data:");
        var dataUrl = CspFixture.DataUrl(DataCssRule);

        Assert.True(csp.AllowsExternalStyle(dataUrl, HttpsPageUrl));

        var importCss = $"@import url({dataUrl}); {OwnCssRule}";
        var html = Page(
            CspFixture.Meta("style-src * 'unsafe-inline' data:") +
            $"<style id=\"s\">{importCss}</style>",
            "<p>text</p>");

        var engine = new ScriptEngine();
        var resultHtml = engine.Execute(["1;"], html, HttpsPageUrl);
        Assert.NotNull(resultHtml);

        var style = ExtractStyleText(resultHtml!);
        Assert.Contains(DataCssRule, style);
        Assert.Contains(OwnCssRule, style);
    }

    // =========================================================================
    //  3. HTTP(S) Stylesheet Under *
    // =========================================================================

    [Fact]
    public void Stylesheet_HttpUrl_AdmittedUnderWildcard()
    {
        // Under style-src *, HTTP(S) stylesheets are admitted
        var csp = CspFixture.CspOf("style-src * 'unsafe-inline'");
        Assert.True(csp.AllowsExternalStyle("https://cdn.example.test/style.css", HttpsPageUrl));
        Assert.True(csp.AllowsExternalStyle("http://cdn.example.test/style.css", HttpsPageUrl));

        // Integration with LoopbackStyleServer
        var sheets = new Dictionary<string, string>
        {
            ["/test.css"] = "#http-target { color: rgb(1, 2, 3); }"
        };
        using var server = new LoopbackStyleServer(sheets);

        var html = Page(
            CspFixture.Meta("style-src * 'unsafe-inline'") +
            $"<style id=\"s\">@import url(/test.css); {OwnCssRule}</style>",
            "<p>text</p>");

        var engine = new ScriptEngine();
        var resultHtml = engine.Execute(["1;"], html, server.PageUrl);
        Assert.NotNull(resultHtml);

        var style = ExtractStyleText(resultHtml!);
        Assert.Contains("#http-target", style);
        Assert.Contains(OwnCssRule, style);
        Assert.Equal(new[] { "/test.css" }, server.RequestedPaths());
    }

    // =========================================================================
    //  4. file: Stylesheet on HTTP(S) Page
    // =========================================================================

    [Fact]
    public void ExternalStyle_FileUrl_RefusedUnderWildcardOnHttpPage()
    {
        // On an https page, file: URL is NOT admitted under style-src *
        var csp = CspFixture.CspOf("style-src *");
        Assert.False(csp.AllowsExternalStyle("file:///styles/sheet.css", HttpsPageUrl));
    }

    [Fact]
    public void ExternalStyle_FileUrl_AdmittedUnderExplicitFileSource()
    {
        // Control: an explicit file: scheme source admits file: URLs
        var csp = CspFixture.CspOf("style-src * file:");
        Assert.True(csp.AllowsExternalStyle("file:///styles/sheet.css", HttpsPageUrl));
    }

    // =========================================================================
    //  5. file: Stylesheet on file: Page
    // =========================================================================

    [Fact]
    public void ExternalStyle_FileUrl_AdmittedUnderWildcardOnFilePage()
    {
        // On a file: page, another file: URL is admitted by * because candidate scheme == origin scheme
        var csp = CspFixture.CspOf("style-src *");
        Assert.True(csp.AllowsExternalStyle("file:///app/sheet.css", FilePageUrl));
    }

    [Fact]
    public void ExternalStyle_DataUrl_RefusedUnderWildcardOnFilePage()
    {
        // On a file: page, data: is STILL refused by * because data is a local scheme
        var csp = CspFixture.CspOf("style-src *");
        Assert.False(csp.AllowsExternalStyle("data:text/css,#a{}", FilePageUrl));
    }

    // =========================================================================
    //  6. AllowsExternalScript Consistency
    // =========================================================================

    [Fact]
    public void AllowsExternalScript_DataUrl_RefusedUnderWildcardWithoutDataSource()
    {
        var csp = CspFixture.CspOf("script-src *");
        Assert.False(csp.AllowsExternalScript("data:text/javascript,console.log(1)", HttpsPageUrl));
    }

    [Fact]
    public void AllowsExternalScript_DataUrl_AdmittedUnderWildcardWithDataSource()
    {
        // Control
        var csp = CspFixture.CspOf("script-src * data:");
        Assert.True(csp.AllowsExternalScript("data:text/javascript,console.log(1)", HttpsPageUrl));
    }

    [Fact]
    public void AllowsExternalScript_FileUrl_RefusedUnderWildcardOnHttpPage()
    {
        var csp = CspFixture.CspOf("script-src *");
        Assert.False(csp.AllowsExternalScript("file:///scripts/main.js", HttpsPageUrl));
    }

    [Fact]
    public void AllowsExternalScript_FileUrl_AdmittedUnderExplicitFileSource()
    {
        // Control
        var csp = CspFixture.CspOf("script-src * file:");
        Assert.True(csp.AllowsExternalScript("file:///scripts/main.js", HttpsPageUrl));
    }

    [Fact]
    public void AllowsExternalScript_FileUrl_AdmittedUnderWildcardOnFilePage()
    {
        var csp = CspFixture.CspOf("script-src *");
        Assert.True(csp.AllowsExternalScript("file:///app/main.js", FilePageUrl));
    }

    [Fact]
    public void AllowsExternalScript_HttpUrl_AdmittedUnderWildcard()
    {
        var csp = CspFixture.CspOf("script-src *");
        Assert.True(csp.AllowsExternalScript("https://cdn.example.test/main.js", HttpsPageUrl));
        Assert.True(csp.AllowsExternalScript("http://cdn.example.test/main.js", HttpsPageUrl));
    }

    // =========================================================================
    //  7. Direct CspSourceMatching.MatchesWildcard Unit Tests
    // =========================================================================

    [Theory]
    [InlineData("http://example.com/a", "https://page.test", true)]
    [InlineData("https://example.com/a", "https://page.test", true)]
    [InlineData("http://example.com/a", null, true)]
    [InlineData("https://example.com/a", null, true)]
    [InlineData("ws://example.com/socket", "https://page.test", true)]
    [InlineData("wss://example.com/socket", "https://page.test", true)]
    [InlineData("ws://example.com/socket", "http://page.test", true)]
    [InlineData("wss://example.com/socket", "http://page.test", true)]
    [InlineData("ws://example.com/socket", "file:///app/index.html", false)]
    [InlineData("ws://example.com/socket", null, false)]
    [InlineData("data:text/plain,hello", "https://page.test", false)]
    [InlineData("data:text/plain,hello", "data:text/plain,world", false)]
    [InlineData("data:text/plain,hello", null, false)]
    [InlineData("blob:https://page.test/uuid", "https://page.test", false)]
    [InlineData("blob:https://page.test/uuid", null, false)]
    [InlineData("file:///path/file.txt", "https://page.test", false)]
    [InlineData("file:///path/file.txt", "file:///app/index.html", true)]
    [InlineData("file:///path/file.txt", null, false)]
    [InlineData("custom-scheme://host/path", "custom-scheme://host/page", true)]
    [InlineData("custom-scheme://host/path", "https://page.test", false)]
    public void MatchesWildcard_ValidatesSpecMatrix(string candidateUrl, string? pageUrl, bool expected)
    {
        var candidate = new Uri(candidateUrl);
        Assert.Equal(expected, CspSourceMatching.MatchesWildcard(candidate, pageUrl));
    }
}
