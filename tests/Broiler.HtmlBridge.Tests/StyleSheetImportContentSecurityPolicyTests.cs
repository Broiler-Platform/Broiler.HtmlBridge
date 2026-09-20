using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Scripting;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Whether the Content-Security-Policy governs the stylesheets a <c>&lt;style&gt;</c> pulls in with
/// <c>@import</c>, which the render projection fetches and inlines so the renderer sees their rules.
/// <para>
/// <b>The import path was not policed at all.</b> A <c>&lt;link rel="stylesheet"&gt;</c> is refused
/// before any request unless <c>style-src-elem</c> → <c>style-src</c> → <c>default-src</c> admits its
/// URL. An <c>@import</c> is the same kind of request — CSS Cascade 4 "fetch an <c>@import</c>" runs
/// css-values-4 "fetch a style resource" with destination <c>style</c>, and CSP3 §6.1.13 names
/// <c>@import</c> as a stylesheet request <c>style-src</c> governs — but the projection fetched
/// <c>http(s)</c>, decoded <c>data:</c> and recursed into nested imports with no check, so a policy
/// that forbade a URL as a <c>&lt;link&gt;</c> admitted the same URL as an import. Every assertion
/// that such an import is "not requested" was answered with a request on the wire.
/// </para>
/// <para>
/// <b>What the policy decides, and what these tests pin.</b> The check is made against the import's
/// URL resolved against the importing sheet's own URL, before the request (so a blocked sheet is
/// never asked for), at every nesting depth, and <c>data:</c> included (Fetch runs the CSP check
/// before it branches to the <c>data:</c> scheme). Three details a naive gate gets wrong, each with
/// Chrome 152 evidence: the importing <c>&lt;style nonce&gt;</c>'s nonce does NOT carry over, because
/// "fetch a style resource" never sets the request's nonce metadata (an empty nonce matches nothing);
/// <c>'unsafe-inline'</c> admits the <c>&lt;style&gt;</c> block but no URL; and a present
/// <c>style-src-elem</c> hides <c>style-src</c> and <c>default-src</c> entirely. A blocked import
/// contributes nothing and stops nothing: later imports and the sheet's own rules still apply.
/// </para>
/// <para>
/// <b>Where the check starts</b> — a <c>&lt;style&gt;</c> before a policy's <c>&lt;meta&gt;</c> keeps its
/// imports, and nothing page script does after parsing moves that boundary — is pinned separately, in
/// <see cref="StyleSheetImportMetaPolicyTests"/>. Every policy here is a header or a meta ahead of the
/// importing block.
/// </para>
/// <para>
/// <b>How it is observed.</b> Page script cannot see an imported rule: <c>getComputedStyle</c> is fed
/// each sheet's raw text and never resolves <c>@import</c>, and layout reads are zero in this harness
/// (and would build extra projections, each fetching again). So every test reads the projected
/// <c>&lt;style&gt;</c> text out of <c>ScriptEngine.Execute</c>'s result, which serializes style text
/// raw, and — for <c>http</c> imports — the request log of a <see cref="LoopbackStyleServer"/> the
/// page is served from, so <c>'self'</c> means that server. A <c>data:</c> import is decoded in
/// process and leaves only the projection to look at.
/// </para>
/// <para>
/// <b>Every prohibition here is paired with a control</b> that runs the same page with no policy (or
/// with the one source that should admit it) and shows the import requested and inlined. A test that
/// asserts a sheet is absent cannot otherwise tell "forbidden" from "never fetched for some other
/// reason" — a mistyped path, a server that never answered, a <c>&lt;style&gt;</c> that Attach removed
/// wholesale. For that last reason each prohibition also asserts the importing sheet's own rule
/// survived.
/// </para>
/// </summary>
public class StyleSheetImportContentSecurityPolicyTests
{
    private const string OkA = "#ok-a { color: rgb(0, 0, 1) }";
    private const string BlockedB = "#blocked-b { color: rgb(0, 0, 2) }";
    private const string OuterRule = "#outer { color: rgb(0, 0, 3) }";
    private const string BlockedInner = "#blocked-inner { color: rgb(0, 0, 4) }";
    private const string OkInner2 = "#ok-inner2 { color: rgb(0, 0, 5) }";
    private const string SameOriginA = "#same-origin { color: rgb(0, 0, 6) }";
    private const string DataRule = "#from-data { color: rgb(0, 0, 7) }";
    private const string NestedDataRule = "#nested-data { color: rgb(0, 0, 8) }";
    private const string OwnRule = "#own { color: rgb(0, 0, 255) }";

    private static readonly string NestedDataImport = $"@import url({CspFixture.DataUrl(NestedDataRule)});";

    /// <summary>Every sheet any test here imports over <c>http</c>.</summary>
    private static readonly Dictionary<string, string> Sheets = new()
    {
        ["/ok/a.css"] = OkA,
        ["/blocked/b.css"] = BlockedB,
        // Relative imports, so they resolve against this sheet's URL and not the page's; the string
        // form is resolved without the url() re-basing the projection applies to imported text.
        ["/ok/outer.css"] = "@import url(../blocked/inner.css); @import \"inner2.css\"; " +
                            NestedDataImport + " " + OuterRule,
        ["/blocked/inner.css"] = BlockedInner,
        ["/ok/inner2.css"] = OkInner2,
        ["/a.css"] = SameOriginA,
    };

    private static string Page(string head) =>
        "<!DOCTYPE html><html><head>" + head + "</head><body><p id=\"p\">p</p></body></html>";

    /// <summary>A page whose one <c>&lt;style id="s"&gt;</c> holds <paramref name="css"/>.</summary>
    private static string StylePage(string? policy, string css, string styleAttributes = "") =>
        Page((policy is null ? string.Empty : CspFixture.Meta(policy)) + $"<style id=\"s\"{styleAttributes}>{css}</style>");

    /// <summary>Runs a script-free page and returns the render projection's HTML.</summary>
    private static string Run(string pageHtml, string pageUrl, ContentSecurityPolicy? headerPolicy = null) =>
        CspFixture.RunProjection(pageHtml, pageUrl, header: headerPolicy);

    /// <summary>The projected text of the <c>&lt;style&gt;</c> with <paramref name="id"/>.</summary>
    private static string StyleText(string html, string id = "s") => CspFixture.StyleText(html, id);

    // ---------------------------------------------------------------------
    //  Host-source paths: which of a sheet's imports a policy admits
    // ---------------------------------------------------------------------

    private static string TwoImports(string first, string second) =>
        $"@import url({first}); @import url({second}); {OwnRule}";

    /// <summary>
    /// A host source with a path admits the imports under that path and no other. The old answer
    /// requested both sheets and inlined both.
    /// </summary>
    [Fact]
    public void AHostSourceWithAPathAdmitsOnlyTheImportsUnderIt()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(
            StylePage($"style-src 'unsafe-inline' {server.Origin}/ok/", TwoImports("/ok/a.css", "/blocked/b.css")),
            server.PageUrl);

        Assert.Equal(new[] { "/ok/a.css" }, server.RequestedPaths());
        var style = StyleText(html);
        Assert.Contains(OkA, style);
        Assert.DoesNotContain(BlockedB, style);
        Assert.Contains(OwnRule, style);
    }

    /// <summary>
    /// A header-delivered policy, which applies to the whole document, gates imports the same way.
    /// The old answer requested and inlined both.
    /// </summary>
    [Fact]
    public void AHeaderDeliveredPolicyGatesImportsTheSameWay()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(
            StylePage(policy: null, TwoImports("/ok/a.css", "/blocked/b.css")),
            server.PageUrl,
            CspFixture.CspOf($"style-src 'unsafe-inline' {server.Origin}/ok/"));

        Assert.Equal(new[] { "/ok/a.css" }, server.RequestedPaths());
        var style = StyleText(html);
        Assert.Contains(OkA, style);
        Assert.DoesNotContain(BlockedB, style);
        Assert.Contains(OwnRule, style);
    }

    /// <summary>The control for the two tests above: with no policy both sheets are requested and inlined.</summary>
    [Fact]
    public void BothImportsAreRequestedAndInlinedUnderNoPolicy()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(StylePage(policy: null, TwoImports("/ok/a.css", "/blocked/b.css")), server.PageUrl);

        Assert.Equal(new[] { "/blocked/b.css", "/ok/a.css" }, server.RequestedPaths());
        var style = StyleText(html);
        Assert.Contains(OkA, style);
        Assert.Contains(BlockedB, style);
        Assert.Contains(OwnRule, style);
    }

    /// <summary>
    /// A blocked import is only that sheet missing: the import after it is still requested and
    /// inlined (Chrome probe C3). The old answer requested and inlined both.
    /// </summary>
    [Fact]
    public void ABlockedFirstImportDoesNotStopTheImportAfterIt()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(
            StylePage($"style-src 'unsafe-inline' {server.Origin}/ok/", TwoImports("/blocked/b.css", "/ok/a.css")),
            server.PageUrl);

        Assert.Equal(new[] { "/ok/a.css" }, server.RequestedPaths());
        var style = StyleText(html);
        Assert.DoesNotContain(BlockedB, style);
        Assert.Contains(OkA, style);
        Assert.Contains(OwnRule, style);
    }

    /// <summary>The control: the same order under no policy requests and inlines both.</summary>
    [Fact]
    public void BothImportsInTheOtherOrderAreRequestedAndInlinedUnderNoPolicy()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(StylePage(policy: null, TwoImports("/blocked/b.css", "/ok/a.css")), server.PageUrl);

        Assert.Equal(new[] { "/blocked/b.css", "/ok/a.css" }, server.RequestedPaths());
        var style = StyleText(html);
        Assert.Contains(BlockedB, style);
        Assert.Contains(OkA, style);
    }

    // ---------------------------------------------------------------------
    //  Nested imports: the same check at every depth
    // ---------------------------------------------------------------------

    private static string OuterImport => $"@import url(/ok/outer.css); {OwnRule}";

    /// <summary>
    /// An admitted sheet's own imports are checked too, each against its URL resolved against that
    /// sheet: <c>../blocked/inner.css</c> is refused, <c>inner2.css</c> resolves under <c>/ok/</c> and
    /// is admitted, and a <c>data:</c> import is refused because nothing lists <c>data:</c> (Chrome
    /// probes C3, C15). The old answer requested <c>/blocked/inner.css</c> and inlined all three.
    /// </summary>
    [Fact]
    public void ANestedImportIsCheckedAgainstThePolicyAtEveryDepth()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(StylePage($"style-src 'unsafe-inline' {server.Origin}/ok/", OuterImport), server.PageUrl);

        Assert.Equal(new[] { "/ok/inner2.css", "/ok/outer.css" }, server.RequestedPaths());
        var style = StyleText(html);
        Assert.DoesNotContain(BlockedInner, style);
        Assert.DoesNotContain(NestedDataRule, style);
        Assert.Contains(OkInner2, style);
        Assert.Contains(OuterRule, style);
        Assert.Contains(OwnRule, style);
    }

    /// <summary>The control: under no policy all three nested imports are inlined.</summary>
    [Fact]
    public void EveryNestedImportIsRequestedAndInlinedUnderNoPolicy()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(StylePage(policy: null, OuterImport), server.PageUrl);

        Assert.Equal(new[] { "/blocked/inner.css", "/ok/inner2.css", "/ok/outer.css" }, server.RequestedPaths());
        var style = StyleText(html);
        Assert.Contains(BlockedInner, style);
        Assert.Contains(NestedDataRule, style);
        Assert.Contains(OkInner2, style);
        Assert.Contains(OuterRule, style);
    }

    // ---------------------------------------------------------------------
    //  Nonces, 'unsafe-inline' and style-src-elem: what does not admit an import URL
    // ---------------------------------------------------------------------

    private static string SameOriginImport => $"@import url(/a.css); {OwnRule}";

    /// <summary>
    /// The <c>&lt;style nonce&gt;</c>'s nonce admits the block, not its imports: "fetch a style
    /// resource" sets no nonce on the request (Chrome probe C1 blocks it and never requests it). The
    /// old answer requested and inlined <c>/a.css</c>.
    /// </summary>
    [Fact]
    public void AStyleElementsNonceDoesNotAuthorizeItsImports()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(StylePage("style-src 'nonce-abc'", SameOriginImport, " nonce=\"abc\""), server.PageUrl);

        Assert.Empty(server.RequestedPaths());
        var style = StyleText(html);
        Assert.DoesNotContain(SameOriginA, style);
        Assert.Contains(OwnRule, style);
    }

    /// <summary>
    /// The control, on the same nonce-carrying block: add <c>'self'</c> and the same-origin import is
    /// admitted (Chrome probe C11), so the test above is about the nonce and not the page.
    /// </summary>
    [Fact]
    public void SelfBesideTheNonceAdmitsTheSameOriginImport()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(StylePage("style-src 'self' 'nonce-abc'", SameOriginImport, " nonce=\"abc\""), server.PageUrl);

        Assert.Equal(new[] { "/a.css" }, server.RequestedPaths());
        var style = StyleText(html);
        Assert.Contains(SameOriginA, style);
        Assert.Contains(OwnRule, style);
    }

    /// <summary>
    /// <c>'unsafe-inline'</c> is read only by the inline checks, so it admits the block and no URL
    /// (Chrome probe C2). The old answer requested and inlined <c>/a.css</c>.
    /// </summary>
    [Fact]
    public void UnsafeInlineAloneDoesNotAuthorizeAnImportUrl()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(StylePage("style-src 'unsafe-inline'", SameOriginImport), server.PageUrl);

        Assert.Empty(server.RequestedPaths());
        var style = StyleText(html);
        Assert.DoesNotContain(SameOriginA, style);
        Assert.Contains(OwnRule, style);
    }

    /// <summary>
    /// An import's effective directive is <c>style-src-elem</c>, and when present it is the only one
    /// consulted: <c>default-src 'self'</c> never gets a say (Chrome probe C5). The old answer requested
    /// and inlined <c>/a.css</c>.
    /// </summary>
    [Fact]
    public void StyleSrcElemHidesTheSelfThatDefaultSrcWouldHaveGranted()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(StylePage("default-src 'self'; style-src-elem 'unsafe-inline'", SameOriginImport), server.PageUrl);

        Assert.Empty(server.RequestedPaths());
        var style = StyleText(html);
        Assert.DoesNotContain(SameOriginA, style);
        Assert.Contains(OwnRule, style);
    }

    /// <summary>
    /// The control for the test above: without <c>style-src-elem</c>, the fallback reaches
    /// <c>default-src</c> and its <c>'self'</c> admits the same import.
    /// </summary>
    [Fact]
    public void DefaultSrcSelfAdmitsTheSameOriginImportWhenNothingHidesIt()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(StylePage("default-src 'self' 'unsafe-inline'", SameOriginImport), server.PageUrl);

        Assert.Equal(new[] { "/a.css" }, server.RequestedPaths());
        Assert.Contains(SameOriginA, StyleText(html));
    }

    /// <summary>The control for the same-origin page: under no policy <c>/a.css</c> is requested and inlined.</summary>
    [Fact]
    public void TheSameOriginImportIsRequestedAndInlinedUnderNoPolicy()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(StylePage(policy: null, SameOriginImport), server.PageUrl);

        Assert.Equal(new[] { "/a.css" }, server.RequestedPaths());
        var style = StyleText(html);
        Assert.Contains(SameOriginA, style);
        Assert.Contains(OwnRule, style);
    }

    // ---------------------------------------------------------------------
    //  data: imports
    // ---------------------------------------------------------------------

    private const string DataPageUrl = "https://example.test/imports";

    private static string DataImport => $"@import url({CspFixture.DataUrl(DataRule)}); {OwnRule}";

    /// <summary>
    /// A <c>data:</c> import is a fetch like any other and needs a <c>data:</c> source: <c>'self'</c>
    /// never matches it (Chrome probe C3). The old answer decoded and inlined it.
    /// </summary>
    [Fact]
    public void ADataImportIsRefusedWithoutADataSource()
    {
        var style = StyleText(Run(StylePage("style-src 'self' 'unsafe-inline'", DataImport), DataPageUrl));

        Assert.DoesNotContain(DataRule, style);
        Assert.Contains(OwnRule, style);
    }

    /// <summary>The control: listing <c>data:</c> admits the same import (Chrome probe C7).</summary>
    [Fact]
    public void ADataSourceAdmitsADataImport()
    {
        var style = StyleText(Run(StylePage("style-src 'unsafe-inline' data:", DataImport), DataPageUrl));

        Assert.Contains(DataRule, style);
        Assert.Contains(OwnRule, style);
    }

    /// <summary>The control under no policy at all.</summary>
    [Fact]
    public void ADataImportIsInlinedUnderNoPolicy()
    {
        var style = StyleText(Run(StylePage(policy: null, DataImport), DataPageUrl));

        Assert.Contains(DataRule, style);
        Assert.Contains(OwnRule, style);
    }

    // ---------------------------------------------------------------------
    //  <link>: the nonce the import path deliberately does not pass
    // ---------------------------------------------------------------------

    private static string LinkPage(string linkAttributes) =>
        Page(CspFixture.Meta("style-src 'nonce-abc'") + $"<link rel=\"stylesheet\" href=\"/a.css\"{linkAttributes}>");

    /// <summary>
    /// The import gate shares its check with the <c>&lt;link rel="stylesheet"&gt;</c> gate and passes no
    /// nonce; a link still passes its own <c>nonce</c> attribute, which HTML "create a link request" copies
    /// onto the request, so under <c>style-src 'nonce-abc'</c> the nonce-carrying link is requested.
    /// Unchanged: this pins that sharing the check did not take the link's nonce away.
    /// </summary>
    [Fact]
    public void ALinksOwnNonceStillAuthorizesItsRequest()
    {
        using var server = new LoopbackStyleServer(Sheets);
        Run(LinkPage(" nonce=\"abc\""), server.PageUrl);

        Assert.Equal(new[] { "/a.css" }, server.RequestedPaths());
    }

    /// <summary>The control: the same link without the nonce is never requested.</summary>
    [Fact]
    public void ALinkWithoutTheNonceIsNotRequested()
    {
        using var server = new LoopbackStyleServer(Sheets);
        Run(LinkPage(string.Empty), server.PageUrl);

        Assert.Empty(server.RequestedPaths());
    }
}
