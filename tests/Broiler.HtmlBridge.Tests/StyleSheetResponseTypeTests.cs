using Broiler.HtmlBridge;
using Broiler.Net.Http;
using Reply = Broiler.HtmlBridge.Tests.LoopbackCookieServer.Reply;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The bridge's own stylesheet loader — the one behind <c>getComputedStyle</c>, <c>cssRules</c> and the
/// render projection's inlined <c>@import</c>s — applies a response only when it is a stylesheet: its
/// <c>Content-Type</c> is <c>text/css</c>, or the requesting document is in quirks mode and the response
/// is CORS-same-origin without <c>X-Content-Type-Options: nosniff</c>. It is the rule Broiler.HTML's
/// loader applies to what it paints.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the rule stops.</b> A <c>&lt;link&gt;</c> or an <c>@import</c> without <c>crossorigin</c> is a
/// no-cors request that carries the user's cookies. Without the check, another site's credentialed HTML
/// or JSON was parsed as a sheet, and whatever rules its text happened to form were readable through
/// the CSSOM — a cross-origin read the page is not entitled to.
/// </para>
/// <para>
/// <b>What is observed.</b> Each response body is a rule that colours <c>#p</c>, so the colour
/// <c>getComputedStyle</c> reports, and the number of rules the sheet exposes, say whether the loader
/// applied it. Every case also asserts the request was made, so a refusal is the check and not a
/// failed fetch.
/// </para>
/// </remarks>
public class StyleSheetResponseTypeTests
{
    private const string Rule = "#p { color: rgb(9, 8, 7) }";
    private const string Applied = "rgb(9, 8, 7)";

    private static BrowserNetworkSession NewProfile() =>
        new(new BrowserNetworkSessionOptions { Timeout = TimeSpan.FromSeconds(20) });

    private static ScriptEngine EngineFor(BrowserNetworkSession profile) =>
        new(new DomBridgeFactory(new DomBridgeSessionOptions { Network = profile, Cookies = profile }));

    /// <summary>A page in standards mode, or — without a doctype — in quirks mode.</summary>
    private static string Page(bool quirks, string head) =>
        (quirks ? string.Empty : "<!DOCTYPE html>") +
        $"<html><head>{head}</head><body><p id=\"p\">p</p><div id=\"out\"></div></body></html>";

    private static Reply Sheet(string contentType, bool noSniff) =>
        new(ContentType: contentType, Body: Rule,
            Headers: noSniff ? [("X-Content-Type-Options", "nosniff")] : null);

    /// <summary>
    /// A <c>&lt;link rel="stylesheet"&gt;</c>, read through <c>getComputedStyle</c> and the sheet's
    /// <c>cssRules</c>. The page is on <c>127.0.0.1</c>; a cross-site sheet is on <c>localhost</c>, and
    /// its no-cors response is opaque, so the quirks-mode exception never reaches it -- and a cross-site
    /// sheet that does apply is not origin-clean, so its rules are not the page's to read.
    /// </summary>
    [Theory]
    // Standards mode: text/css only.
    [InlineData(false, false, "text/css", false, true)]
    [InlineData(false, false, "text/css", true, true)]
    [InlineData(false, false, "text/plain", false, false)]
    [InlineData(false, true, "text/css", false, true)]
    [InlineData(false, true, "text/html", false, false)]
    [InlineData(false, true, "application/json", false, false)]
    // Quirks mode: a same-origin response of any type, unless nosniff; never an opaque one.
    [InlineData(true, false, "text/plain", false, true)]
    [InlineData(true, false, "text/plain", true, false)]
    [InlineData(true, true, "text/plain", false, false)]
    [InlineData(true, true, "text/css", false, true)]
    public void ALinkedSheetAppliesOnlyWhenItIsAStylesheet(
        bool quirks, bool crossSite, string contentType, bool noSniff, bool applies)
    {
        using var server = new LoopbackCookieServer().Map("/sheet", Sheet(contentType, noSniff));
        using var profile = NewProfile();
        var href = crossSite ? server.LocalhostUrl("/sheet") : "/sheet";

        var rendered = EngineFor(profile).Execute(
            [PageProbe.Probe(
                "getComputedStyle(document.getElementById('p')).color + '|' +" +
                " (function () { try { return document.styleSheets[0].cssRules.length; } catch (e) { return e.name; } })()")],
            Page(quirks, $"<link rel=\"stylesheet\" href=\"{href}\">"),
            server.Url("/page"));

        Assert.NotEmpty(server.RequestsFor("/sheet"));
        var (color, rules) = Split(PageProbe.OutOf(rendered!));
        if (applies)
        {
            Assert.Equal(Applied, color);
            Assert.Equal(crossSite ? "SecurityError" : "1", rules);
        }
        else
        {
            Assert.NotEqual(Applied, color);
            Assert.Equal("0", rules);
        }
    }

    /// <summary>
    /// A <c>&lt;style&gt;</c>'s <c>@import</c>, which the bridge loads twice: for <c>getComputedStyle</c>
    /// (the computed-style scope's loader) and for the render projection, which inlines the imported
    /// text. Neither may apply a cross-site response that is not <c>text/css</c>.
    /// </summary>
    [Theory]
    [InlineData("text/css", true)]
    [InlineData("text/html", false)]
    public void AnImportedSheetAppliesOnlyWhenItIsAStylesheet(string contentType, bool applies)
    {
        using var server = new LoopbackCookieServer().Map("/imported", Sheet(contentType, noSniff: false));
        using var profile = NewProfile();

        var rendered = EngineFor(profile).Execute(
            [PageProbe.Probe("getComputedStyle(document.getElementById('p')).color")],
            Page(quirks: false, $"<style id=\"s\">@import url(\"{server.LocalhostUrl("/imported")}\");</style>"),
            server.Url("/page"));

        Assert.NotEmpty(server.RequestsFor("/imported"));
        if (applies)
        {
            Assert.Equal(Applied, PageProbe.OutOf(rendered!));
            Assert.Contains(Rule, CspFixture.StyleText(rendered!));
        }
        else
        {
            Assert.NotEqual(Applied, PageProbe.OutOf(rendered!));
            Assert.DoesNotContain(Rule, CspFixture.StyleText(rendered!));
        }
    }

    /// <summary>
    /// An <c>@import</c> inside a linked sheet is checked on its own: the linked sheet is <c>text/css</c>
    /// and applies, the cross-site JSON it imports does not.
    /// </summary>
    [Fact]
    public void AnImportInsideALinkedSheetIsCheckedOnItsOwn()
    {
        using var server = new LoopbackCookieServer();
        server
            .Map("/outer.css", new Reply(ContentType: "text/css",
                Body: $"@import url(\"{server.LocalhostUrl("/secret")}\"); #q {{ color: rgb(1, 2, 3) }}"))
            .Map("/secret", Sheet("application/json", noSniff: false));
        using var profile = NewProfile();

        var rendered = EngineFor(profile).Execute(
            [PageProbe.Probe(
                "getComputedStyle(document.getElementById('p')).color + '|' + getComputedStyle(document.getElementById('q')).color")],
            "<!DOCTYPE html><html><head><link rel=\"stylesheet\" href=\"/outer.css\"></head>" +
            "<body><p id=\"p\">p</p><p id=\"q\">q</p><div id=\"out\"></div></body></html>",
            server.Url("/page"));

        Assert.NotEmpty(server.RequestsFor("/secret"));
        var (p, q) = Split(PageProbe.OutOf(rendered!));
        Assert.NotEqual(Applied, p);
        Assert.Equal("rgb(1, 2, 3)", q);
    }

    private static (string First, string Second) Split(string probe)
    {
        var parts = probe.Split('|');
        Assert.Equal(2, parts.Length);
        return (parts[0], parts[1]);
    }
}
