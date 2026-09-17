using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Scripting;

// Aliased: inside namespace Broiler.*, a bare Regex binds to the Broiler.Regex namespace first.
using TextRegex = System.Text.RegularExpressions.Regex;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Which <c>&lt;style&gt;</c> elements' <c>@import</c> requests a Content-Security-Policy binds, when the
/// policy may have arrived in a <c>&lt;meta http-equiv="Content-Security-Policy"&gt;</c> part-way through
/// the document. <see cref="StyleSheetImportContentSecurityPolicyTests"/> pins what the check admits;
/// this pins where it starts.
/// <para>
/// <b>A meta policy is not retroactive.</b> A browser enforces it from the point the parser reaches the
/// meta. A <c>&lt;style&gt;</c> before it has already been applied, and its imports requested, so the
/// Attach-time element gate keeps that block and the import gate lets its imports through. A header
/// policy binds the whole document, and so does a policy whose meta the parser never recognized.
/// </para>
/// <para>
/// <b>Only what the parser saw decides.</b> The projection that inlines imports is built after page
/// script has run, from a tree script may have rewritten. A gate that looked for the meta in that tree
/// let script decide the boundary: a CSP meta inserted after parsing switched a header policy off for
/// every block before it, moving the meta exempted blocks it never preceded, and removing it blocked
/// imports a browser had already fetched. In a browser a delivered policy stays in the document's policy
/// list whatever later happens to its meta, and a meta only ever adds a policy to the ones already
/// enforced, never lifts a header's (CSP3 §3.3). So the exemption is what the Attach-time gate
/// recorded: the source blocks it found before the meta, and the import URLs they held then. A block
/// script inserts, and a URL script writes into an exempt block, are checked like any other. The
/// imports inside an exempt block's imported sheets are checked too: their requests go out when that
/// sheet arrives, which is normally after the parser has passed the meta.
/// </para>
/// <para>
/// <b>How it is observed</b>, as in <see cref="StyleSheetImportContentSecurityPolicyTests"/>: the
/// request log of a <see cref="LoopbackStyleServer"/> the page is served from, and the projected
/// <c>&lt;style&gt;</c> text in <c>ScriptEngine.Execute</c>'s result. Every prohibition is paired with a
/// control that runs the same page and script with no policy and shows the import requested.
/// </para>
/// </summary>
public class StyleSheetImportMetaPolicyTests
{
    private const string EarlyRule = "#early { color: rgb(0, 0, 9) }";
    private const string LateRule = "#late { color: rgb(0, 0, 10) }";
    private const string BlockedB = "#blocked-b { color: rgb(0, 0, 2) }";
    private const string EarlyOuterRule = "#early-outer { color: rgb(0, 0, 11) }";
    private const string BlockedInner = "#blocked-inner { color: rgb(0, 0, 4) }";
    private const string OwnRule = "#own { color: rgb(0, 0, 255) }";
    private const string EarlyOwnRule = "#early-own { color: rgb(0, 0, 254) }";
    private const string LateOwnRule = "#late-own { color: rgb(0, 0, 253) }";

    private static readonly Dictionary<string, string> Sheets = new()
    {
        ["/early.css"] = EarlyRule,
        ["/late.css"] = LateRule,
        ["/blocked/b.css"] = BlockedB,
        ["/early-outer.css"] = "@import url(blocked/inner.css); " + EarlyOuterRule,
        ["/blocked/inner.css"] = BlockedInner,
        ["/blocked/early.css"] = BlockedB,
    };

    private const string UnsafeInlineOnly = "style-src 'unsafe-inline'";

    private static string Meta(string policy, string httpEquiv = "Content-Security-Policy") =>
        $"<meta http-equiv=\"{httpEquiv}\" content=\"{policy}\">";

    private static string Page(string head) =>
        "<!DOCTYPE html><html><head>" + head + "</head><body><p id=\"p\">p</p></body></html>";

    private static string EarlyAndLatePage(string? policy, string earlyImport = "/early.css") =>
        Page(
            $"<style id=\"early\">@import url({earlyImport}); {EarlyOwnRule}</style>" +
            (policy is null ? string.Empty : Meta(policy)) +
            $"<style id=\"late\">@import url(/late.css); {LateOwnRule}</style>");

    /// <summary>Runs <paramref name="script"/> on the page and returns the render projection's HTML.</summary>
    private static string Run(
        string pageHtml, string pageUrl, string script = "1;", string? headerPolicy = null)
    {
        var engine = new ScriptEngine();
        if (headerPolicy is not null)
        {
            engine.Csp = new ContentSecurityPolicy();
            engine.Csp.Parse(headerPolicy);
        }

        var html = engine.Execute([script], pageHtml, pageUrl);
        Assert.NotNull(html);
        return html!;
    }

    /// <summary>The projected text of the <c>&lt;style&gt;</c> with <paramref name="id"/>.</summary>
    private static string StyleText(string html, string id)
    {
        var match = TextRegex.Match(html, $"<style[^>]*\\bid=\"{id}\"[^>]*>([\\s\\S]*?)</style>");
        Assert.True(match.Success, $"no <style id=\"{id}\"> in the projection: {html}");
        return match.Groups[1].Value;
    }

    // ---------------------------------------------------------------------
    //  A meta policy is not retroactive
    // ---------------------------------------------------------------------

    /// <summary>
    /// A <c>&lt;style&gt;</c> before the policy's <c>&lt;meta&gt;</c> is exempt, and so are its imports;
    /// one after it is bound, and so are its. Both blocks survive Attach here (<c>'unsafe-inline'</c>), so
    /// only the import gate can tell them apart. The old answer requested and inlined <c>/late.css</c>
    /// as well.
    /// </summary>
    [Fact]
    public void AStyleBeforeTheMetaKeepsItsImportAndAStyleAfterItDoesNot()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(EarlyAndLatePage(UnsafeInlineOnly), server.PageUrl);

        Assert.Equal(new[] { "/early.css" }, server.RequestedPaths());
        var early = StyleText(html, "early");
        Assert.Contains(EarlyRule, early);
        Assert.Contains(EarlyOwnRule, early);
        var late = StyleText(html, "late");
        Assert.DoesNotContain(LateRule, late);
        Assert.Contains(LateOwnRule, late);
    }

    /// <summary>
    /// What the element gate already does with the same page under <c>style-src 'none'</c>, and what
    /// the import gate must agree with: the block before the meta is kept, and its import with it (it
    /// was requested and inlined before the import gate existed); the block after it is removed whole,
    /// so its import is never requested. A control: this answer is unchanged.
    /// </summary>
    [Fact]
    public void AStyleBeforeAMetaForbiddingAllStylesKeepsItselfAndItsImport()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(EarlyAndLatePage("style-src 'none'"), server.PageUrl);

        Assert.Equal(new[] { "/early.css" }, server.RequestedPaths());
        var early = StyleText(html, "early");
        Assert.Contains(EarlyRule, early);
        Assert.Contains(EarlyOwnRule, early);
        Assert.DoesNotContain("id=\"late\"", html);
    }

    /// <summary>The control: with no meta both blocks' imports are requested and inlined.</summary>
    [Fact]
    public void BothBlocksImportsAreRequestedAndInlinedUnderNoPolicy()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(EarlyAndLatePage(policy: null), server.PageUrl);

        Assert.Equal(new[] { "/early.css", "/late.css" }, server.RequestedPaths());
        Assert.Contains(EarlyRule, StyleText(html, "early"));
        Assert.Contains(LateRule, StyleText(html, "late"));
    }

    /// <summary>
    /// The exemption covers the exempt block's own imports, not the imports inside the sheets they
    /// bring in: <c>/early-outer.css</c> is requested and inlined, and its <c>blocked/inner.css</c> is
    /// checked, refused and never requested. The old answer requested and inlined both.
    /// </summary>
    [Fact]
    public void AnExemptBlocksImportedSheetHasItsOwnImportsChecked()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(EarlyAndLatePage(UnsafeInlineOnly, "/early-outer.css"), server.PageUrl);

        Assert.Equal(new[] { "/early-outer.css" }, server.RequestedPaths());
        var early = StyleText(html, "early");
        Assert.Contains(EarlyOuterRule, early);
        Assert.DoesNotContain(BlockedInner, early);
        Assert.Contains(EarlyOwnRule, early);
    }

    /// <summary>The control: under no policy both the outer and the inner sheet are requested and inlined.</summary>
    [Fact]
    public void TheOuterAndInnerSheetAreRequestedAndInlinedUnderNoPolicy()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(EarlyAndLatePage(policy: null, "/early-outer.css"), server.PageUrl);

        Assert.Equal(new[] { "/blocked/inner.css", "/early-outer.css", "/late.css" }, server.RequestedPaths());
        var early = StyleText(html, "early");
        Assert.Contains(EarlyOuterRule, early);
        Assert.Contains(BlockedInner, early);
    }

    // ---------------------------------------------------------------------
    //  Only what the parser saw decides
    // ---------------------------------------------------------------------

    /// <summary>
    /// A script that appends a <c>&lt;style&gt;</c> and, after it, a CSP <c>&lt;meta&gt;</c> in the body.
    /// A meta only ever adds a policy to those already enforced (CSP3 §3.3), and HTML ignores one outside
    /// <c>&lt;head&gt;</c>, so nothing here can lift the header policy.
    /// </summary>
    private const string InsertStyleThenMeta =
        "var d = document.createElement('div');" +
        "d.innerHTML = '<style id=\"inserted\">@import url(/blocked/b.css); " + OwnRule + "</style>" +
        "<meta http-equiv=\"Content-Security-Policy\" content=\"style-src *\">';" +
        "document.body.appendChild(d);";

    /// <summary>
    /// A header policy binds the whole document, and a CSP meta that script inserts cannot exempt the
    /// block in front of it. A gate that looked for the meta in the projected tree requested and inlined
    /// <c>/blocked/b.css</c>, whatever the meta said.
    /// </summary>
    [Fact]
    public void AScriptInsertedMetaDoesNotExemptABlockFromAHeaderPolicy()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(Page(string.Empty), server.PageUrl, InsertStyleThenMeta,
            headerPolicy: $"style-src 'unsafe-inline' {server.Origin}/ok/");

        Assert.Empty(server.RequestedPaths());
        var style = StyleText(html, "inserted");
        Assert.DoesNotContain(BlockedB, style);
        Assert.Contains(OwnRule, style);
    }

    /// <summary>The control: the same script under no policy requests and inlines the import.</summary>
    [Fact]
    public void TheInsertedBlocksImportIsRequestedAndInlinedUnderNoPolicy()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(Page(string.Empty), server.PageUrl, InsertStyleThenMeta);

        Assert.Equal(new[] { "/blocked/b.css" }, server.RequestedPaths());
        Assert.Contains(BlockedB, StyleText(html, "inserted"));
    }

    private static string BlockThenUnrecognizedMeta =>
        Page($"<style id=\"s\">@import url(/blocked/b.css); {OwnRule}</style>" +
             Meta("style-src *", httpEquiv: "Content-Security-Policy "));

    /// <summary>
    /// HTML matches <c>http-equiv</c> without trimming it, so a meta whose value ends in a space delivers
    /// no policy (the policy discovery ignores it), and the header policy binds the block before it. The
    /// old answer trimmed the value when looking for the meta in the tree, exempted the block from the
    /// header policy, and requested and inlined <c>/blocked/b.css</c>.
    /// </summary>
    [Fact]
    public void AMetaThePolicyDiscoveryIgnoresDoesNotExemptABlockFromAHeaderPolicy()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(BlockThenUnrecognizedMeta, server.PageUrl,
            headerPolicy: $"style-src 'unsafe-inline' {server.Origin}/ok/");

        Assert.Empty(server.RequestedPaths());
        var style = StyleText(html, "s");
        Assert.DoesNotContain(BlockedB, style);
        Assert.Contains(OwnRule, style);
    }

    /// <summary>
    /// The element gate reads the meta the same way, since the import gate reuses its walk: under a header
    /// <c>style-src 'none'</c> the block before the ignored meta is removed, as every block is. The old
    /// answer kept it, exempt from the header policy. Its control is the test below, where it survives.
    /// </summary>
    [Fact]
    public void AMetaThePolicyDiscoveryIgnoresDoesNotExemptABlockFromTheElementGateEither()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(BlockThenUnrecognizedMeta, server.PageUrl, headerPolicy: "style-src 'none'");

        Assert.Empty(server.RequestedPaths());
        Assert.DoesNotContain("id=\"s\"", html);
    }

    /// <summary>The control: the same page under no policy requests and inlines the import.</summary>
    [Fact]
    public void TheBlockBeforeTheIgnoredMetaImportsUnderNoPolicy()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(BlockThenUnrecognizedMeta, server.PageUrl);

        Assert.Equal(new[] { "/blocked/b.css" }, server.RequestedPaths());
        Assert.Contains(BlockedB, StyleText(html, "s"));
    }

    private const string RewriteEarlyBlock =
        "document.getElementById('early').textContent = '@import url(/blocked/b.css); " + EarlyOwnRule + "';";

    /// <summary>
    /// An exempt block keeps the imports it held when the parser read it, not whatever script writes
    /// into it later: a browser requests a rewritten block's imports under the policy in force by then.
    /// A gate that exempted the element whatever its text requested and inlined <c>/blocked/b.css</c>.
    /// </summary>
    [Fact]
    public void AnImportScriptWritesIntoAnExemptBlockIsChecked()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(EarlyAndLatePage(UnsafeInlineOnly), server.PageUrl, RewriteEarlyBlock);

        Assert.Empty(server.RequestedPaths());
        var early = StyleText(html, "early");
        Assert.DoesNotContain(BlockedB, early);
        Assert.Contains(EarlyOwnRule, early);
    }

    /// <summary>The control: the same rewrite under no policy requests and inlines the new import.</summary>
    [Fact]
    public void TheRewrittenImportIsRequestedAndInlinedUnderNoPolicy()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(EarlyAndLatePage(policy: null), server.PageUrl, RewriteEarlyBlock);

        Assert.Equal(new[] { "/blocked/b.css", "/late.css" }, server.RequestedPaths());
        Assert.Contains(BlockedB, StyleText(html, "early"));
    }

    /// <summary>
    /// Moving the meta to the end of the body after parsing exempts nothing new: the late block was
    /// parsed after the meta and stays bound. A gate that looked for the meta in the projected tree
    /// found both blocks before it and requested <c>/late.css</c>. The control is
    /// <see cref="BothBlocksImportsAreRequestedAndInlinedUnderNoPolicy"/>, where this script finds no
    /// meta to move.
    /// </summary>
    [Fact]
    public void MovingTheMetaAfterParsingExemptsNoFurtherBlock()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(EarlyAndLatePage(UnsafeInlineOnly), server.PageUrl,
            "var m = document.querySelector('meta'); document.body.appendChild(m);");

        Assert.Equal(new[] { "/early.css" }, server.RequestedPaths());
        Assert.DoesNotContain(LateRule, StyleText(html, "late"));
        Assert.Contains(EarlyRule, StyleText(html, "early"));
    }

    /// <summary>
    /// Removing the meta after parsing takes no exemption away: the early block's import is still
    /// requested and inlined, as it is with the meta in place
    /// (<see cref="AStyleBeforeTheMetaKeepsItsImportAndAStyleAfterItDoesNot"/>), and the late block is
    /// still bound. A gate that looked for the meta in the projected tree found none, treated the policy
    /// as a header's, and requested nothing.
    /// </summary>
    [Fact]
    public void RemovingTheMetaAfterParsingKeepsTheExemptBlocksImport()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(EarlyAndLatePage(UnsafeInlineOnly), server.PageUrl,
            "var m = document.querySelector('meta'); m.parentNode.removeChild(m);");

        Assert.Equal(new[] { "/early.css" }, server.RequestedPaths());
        Assert.Contains(EarlyRule, StyleText(html, "early"));
        Assert.DoesNotContain(LateRule, StyleText(html, "late"));
    }

    private const string AddBase =
        "var b = document.createElement('base'); b.setAttribute('href', '/blocked/'); document.head.appendChild(b);";

    /// <summary>
    /// The exemption is for the URLs the block named when it was parsed. A <c>&lt;base&gt;</c> that
    /// script adds re-points the block's relative <c>early.css</c> at <c>/blocked/early.css</c> when the
    /// projection resolves it again, and that URL was never requested before the meta, so it is checked
    /// and refused. A gate that exempted the element whatever it now resolved to requested it.
    /// </summary>
    [Fact]
    public void AnExemptImportThatABaseAddedByScriptRepointsIsChecked()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(EarlyAndLatePage(UnsafeInlineOnly, "early.css"), server.PageUrl, AddBase);

        Assert.Empty(server.RequestedPaths());
        var early = StyleText(html, "early");
        Assert.DoesNotContain(BlockedB, early);
        Assert.Contains(EarlyOwnRule, early);
    }

    /// <summary>The control: under no policy the re-pointed import is requested and inlined.</summary>
    [Fact]
    public void TheRepointedImportIsRequestedAndInlinedUnderNoPolicy()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(EarlyAndLatePage(policy: null, "early.css"), server.PageUrl, AddBase);

        Assert.Equal(new[] { "/blocked/early.css", "/late.css" }, server.RequestedPaths());
        Assert.Contains(BlockedB, StyleText(html, "early"));
    }

    private const string InsertStyleBeforeMeta =
        "var s = document.createElement('style'); s.id = 'inserted';" +
        "s.textContent = '@import url(/blocked/b.css); " + OwnRule + "';" +
        "document.head.insertBefore(s, document.head.firstChild);";

    /// <summary>
    /// A block script inserts in front of the meta was never parsed before it, so it is bound. A gate
    /// that looked for the meta in the projected tree requested and inlined <c>/blocked/b.css</c>.
    /// </summary>
    [Fact]
    public void ABlockScriptInsertsBeforeTheMetaIsBound()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(Page(Meta(UnsafeInlineOnly)), server.PageUrl, InsertStyleBeforeMeta);

        Assert.Empty(server.RequestedPaths());
        var style = StyleText(html, "inserted");
        Assert.DoesNotContain(BlockedB, style);
        Assert.Contains(OwnRule, style);
    }

    /// <summary>The control: the same insertion under no policy requests and inlines the import.</summary>
    [Fact]
    public void TheBlockInsertedAtTheTopImportsUnderNoPolicy()
    {
        using var server = new LoopbackStyleServer(Sheets);
        var html = Run(Page(string.Empty), server.PageUrl, InsertStyleBeforeMeta);

        Assert.Equal(new[] { "/blocked/b.css" }, server.RequestedPaths());
        Assert.Contains(BlockedB, StyleText(html, "inserted"));
    }
}
