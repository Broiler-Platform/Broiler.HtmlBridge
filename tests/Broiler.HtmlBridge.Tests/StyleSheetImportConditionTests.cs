using System.Text;

using Broiler.CSS;
using Broiler.CSS.Dom;
using Broiler.Dom.Html;
using Broiler.HtmlBridge;

// Aliased: inside namespace Broiler.*, a bare Regex binds to the Broiler.Regex namespace first.
using TextRegex = System.Text.RegularExpressions.Regex;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// What the render projection does with an <c>@import</c>'s <c>layer</c>, <c>supports()</c> and media
/// list when it inlines the imported sheet into the importing <c>&lt;style&gt;</c>.
/// <para>
/// <b>Every condition used to become a media query.</b> CSS Cascade 5 §2 gives the prelude a fixed
/// order — <c>@import [ &lt;url&gt; | &lt;string&gt; ] [ layer | layer(&lt;layer-name&gt;) ]?
/// [ supports( … ) ]? &lt;media-query-list&gt;?</c> — but the projection took everything after the URL
/// as the media text and wrapped the sheet as <c>@media &lt;rest&gt; { … }</c>. <c>layer</c> is a
/// reserved media-type ident and <c>layer(…)</c>/<c>supports(…)</c> are general-enclosed, so each
/// such query is false and the imported rules vanished from paint.
/// </para>
/// <para>
/// <b>What these tests pin.</b> The prelude is split in grammar order; <c>supports(X)</c> is decided
/// up front (a false one is skipped with no request, as Cascade 5 requires, and a true one needs no
/// wrapper); only a non-empty media list is still emitted as <c>@media</c>; and <c>layer</c> emits no
/// wrapper at all, because the consumed <c>CssStyleEngine</c> discards every rule inside an
/// <c>@layer</c> block, so a layered import is inlined unlayered — the rules apply, the layer ordering
/// does not. Parts out of order, or a malformed <c>layer(…)</c>, are not consumed: they stay in the
/// media text and evaluate false, which is what Chrome 152 does (probes L6, L7). Two scanner gaps in
/// the same prelude are pinned beside them: a quoted <c>url("…")</c> containing <c>)</c>, and
/// <c>@layer</c> statement rules, which may precede the imports and end the run after one.
/// </para>
/// <para>
/// <b>How it is observed.</b> As in <see cref="StyleSheetImportContentSecurityPolicyTests"/>: page
/// script never sees an imported rule, so each test reads the projected <c>&lt;style&gt;</c> text out of
/// <c>ScriptEngine.Execute</c>. Where the claim is that a rule applies (or does not), the projected text
/// is also run through the engine the renderer cascades with — <c>CssParser</c> into a
/// <c>CssStyleEngine</c>, the shape Broiler.HTML builds for a render — and <c>#p</c>'s cascaded
/// <c>color</c> is read back, so a wrapper that looks plausible and evaluates false cannot pass.
/// Imported sheets are base64 <c>data:</c> URLs (no raw <c>)</c> inside <c>url()</c>) unless the test
/// needs a request log, which a <see cref="LoopbackStyleServer"/> provides. No policy is set anywhere.
/// </para>
/// </summary>
public class StyleSheetImportConditionTests
{
    private const string PageUrl = "https://example.test/import-conditions";

    private const string Green = "rgb(0, 128, 0)";
    private const string Imported = "#p { color: rgb(0, 128, 0) }";
    private const string OwnRule = "#own { color: rgb(0, 0, 255) }";
    private const string Blue = "rgb(0, 0, 255)";

    /// <summary>A second imported rule, for tests that import two sheets; nothing on the page matches it.</summary>
    private const string AlsoImported = "#also { color: rgb(0, 128, 0) }";

    private static string DataUrl(string css) =>
        "data:text/css;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(css));

    private static readonly string ImportedUrl = DataUrl(Imported);
    private static readonly string AlsoImportedUrl = DataUrl(AlsoImported);

    private static string Page(string css) =>
        $"<!DOCTYPE html><html><head><style id=\"s\">{css}</style></head>" +
        "<body><p id=\"p\">p</p><p id=\"own\">own</p></body></html>";

    /// <summary>
    /// The projected text of <c>&lt;style id="s"&gt;</c> after a script-free run (one <c>1;</c>
    /// script, because <c>Execute</c> answers <see langword="null"/> for none).
    /// </summary>
    private static string ProjectedStyle(string css, string pageUrl = PageUrl)
    {
        var html = new ScriptEngine().Execute(["1;"], Page(css), pageUrl);
        Assert.NotNull(html);

        var match = TextRegex.Match(html!, "<style[^>]*\\bid=\"s\"[^>]*>([\\s\\S]*?)</style>");
        Assert.True(match.Success, $"no <style id=\"s\"> in the projection: {html}");
        return match.Groups[1].Value;
    }

    /// <summary>
    /// <paramref name="id"/>'s cascaded <c>color</c> when <paramref name="styleText"/> is the author
    /// sheet, or <see langword="null"/> when no rule sets it — through the consumed parser and cascade,
    /// on an 800×600 screen.
    /// </summary>
    private static string? CascadedColor(string styleText, string id = "p")
    {
        var document = HtmlDocumentParser.ParseDocument(Page(string.Empty)).Document;
        var engine = new CssStyleEngine();
        engine.AddStyleSheet(new CssParser().ParseStyleSheet(styleText), CssOrigin.Author);
        engine.UpdateEnvironment(new CssEnvironment(800, 600));
        var element = document.GetElementById(id);
        Assert.NotNull(element);
        return engine.GetCascadedStyle(element!, includeInlineStyle: true).TryGetValue("color", out var color)
            ? color
            : null;
    }

    /// <summary>The imported rule is in the projection with no group rule of any kind around it.</summary>
    private static void AssertInlinedUnwrapped(string style)
    {
        Assert.Contains(Imported, style);
        Assert.DoesNotContain("@media", style);
        Assert.DoesNotContain("@layer", style);
        Assert.DoesNotContain("@supports", style);
        Assert.DoesNotContain("@import", style);
    }

    // ---------------------------------------------------------------------
    //  layer
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>layer(base)</c> is consumed as the layer, and inlined wrapped in <c>@layer base { … }</c>
    /// per CSS Cascade 5.
    /// </summary>
    [Fact]
    public void ANamedLayerImportIsInlinedInLayerAndApplies()
    {
        var style = ProjectedStyle($"@import url({ImportedUrl}) layer(base); {OwnRule}");

        Assert.Contains("@layer base {", style);
        Assert.Contains(Imported, style);
        Assert.Contains(OwnRule, style);
        Assert.Equal(Green, CascadedColor(style));
    }

    /// <summary>
    /// The bare <c>layer</c> keyword (an anonymous layer), here after a string URL, is wrapped
    /// in <c>@layer { … }</c>.
    /// </summary>
    [Fact]
    public void AnAnonymousLayerImportIsInlinedInLayerAndApplies()
    {
        var style = ProjectedStyle($"@import \"{ImportedUrl}\" layer; {OwnRule}");

        Assert.Contains("@layer {", style);
        Assert.Contains(Imported, style);
        Assert.Equal(Green, CascadedColor(style));
    }

    /// <summary>
    /// A <c>&lt;layer-name&gt;</c> is a dotted ident chain, wrapped in <c>@layer &lt;name&gt; { … }</c>.
    /// </summary>
    [Fact]
    public void ADottedLayerNameIsInlinedInLayerAndApplies()
    {
        var style = ProjectedStyle($"@import url({ImportedUrl}) layer(theme.base);");

        Assert.Contains("@layer theme.base {", style);
        Assert.Contains(Imported, style);
        Assert.Equal(Green, CascadedColor(style));
    }

    /// <summary>
    /// A layer name must start like an ident, so <c>layer(1bad)</c> is not a layer, and a comment
    /// between two idents leaves two idents, so <c>layer(a/**/b)</c> is not one either: each stays in the
    /// media text, where it is false (Chrome probe L7). A control: the old projection was the same
    /// <c>@media layer(1bad) { … }</c>, and it guards against a parser that takes any
    /// <c>layer(…)</c> as a layer.
    /// </summary>
    [Fact]
    public void AnInvalidLayerNameStaysInTheMediaTextAndDoesNotApply()
    {
        var style = ProjectedStyle(
            $"@import url({ImportedUrl}) layer(1bad); @import url({AlsoImportedUrl}) layer(a/**/b); {OwnRule}");

        Assert.Contains("@media layer(1bad) {", style);
        Assert.Contains("@media layer(a/**/b) {", style);
        Assert.Null(CascadedColor(style));
        Assert.Equal("rgb(0, 0, 255)", CascadedColor(style, "own"));
    }

    /// <summary>
    /// A comment between a layer name's segments separates tokens without adding whitespace, so
    /// <c>layer(theme/**/.base)</c> is the layer <c>theme.base</c>, as Chromium's tokenizer reads it; and a
    /// hex escape with its trailing space is one code point, so <c>layer(\31 st)</c> is the ident
    /// <c>1st</c>. Both are inlined in their respective @layer blocks.
    /// </summary>
    [Fact]
    public void ALayerNameMayHoldACommentBetweenSegmentsOrAHexEscape()
    {
        var style = ProjectedStyle(
            $"@import url({ImportedUrl}) layer(theme/**/.base); @import url({AlsoImportedUrl}) layer(\\31 st);");

        Assert.Contains("@layer theme.base {", style);
        Assert.Contains("@layer \\31 st {", style);
        Assert.Contains(AlsoImported, style);
        Assert.Equal(Green, CascadedColor(style));
    }

    /// <summary>
    /// Broiler.CSS.Dom implements cascade layers per CSS Cascade 5: rules inside an @layer
    /// block, named or anonymous, participate in the cascade with layer ordering.
    /// </summary>
    [Fact]
    public void TheConsumedCascadeAppliesRulesInsideLayerBlocks()
    {
        Assert.Equal(Green, CascadedColor($"@layer base {{ {Imported} }}"));
        Assert.Equal(Green, CascadedColor($"@layer {{ {Imported} }}"));
        Assert.Equal(Green, CascadedColor(Imported));
    }

    // ---------------------------------------------------------------------
    //  supports()
    // ---------------------------------------------------------------------

    /// <summary>
    /// A true <c>supports()</c> is decided at inline time and leaves no wrapper. The old answer was
    /// <c>@media supports(display: grid) { … }</c>.
    /// </summary>
    [Fact]
    public void ATrueSupportsImportIsInlinedUnwrappedAndApplies()
    {
        var style = ProjectedStyle($"@import url({ImportedUrl}) supports(display: grid); {OwnRule}");

        AssertInlinedUnwrapped(style);
        Assert.Equal(Green, CascadedColor(style));
    }

    /// <summary>
    /// A <c>&lt;supports-condition&gt;</c> with nested connectives, not only a bare declaration. The
    /// old answer wrapped it as <c>@media supports((display: grid) and …) { … }</c>.
    /// </summary>
    [Fact]
    public void ASupportsConditionWithNestedConnectivesIsEvaluated()
    {
        var style = ProjectedStyle(
            $"@import url({ImportedUrl}) supports((display: grid) and (not (display: bogus)));");

        AssertInlinedUnwrapped(style);
        Assert.Equal(Green, CascadedColor(style));
    }

    /// <summary>
    /// A comment inside <c>supports()</c> is not part of the condition: CSS drops comments as it
    /// tokenizes, and the renderer reads an <c>@supports</c> prelude with its comments removed. The
    /// evaluator answers false for any condition that still holds one, so a gate that passed the raw text
    /// skipped both imports, requesting and inlining nothing, where Chrome 152 applies them. The old
    /// answer wrapped them as <c>@media supports(/* c */ display: grid) { … }</c>.
    /// </summary>
    [Fact]
    public void ACommentInsideSupportsDoesNotMakeItFalse()
    {
        var style = ProjectedStyle(
            $"@import url({ImportedUrl}) supports(/* c */ display: grid); " +
            $"@import url({AlsoImportedUrl}) supports(display: grid /* modern */); {OwnRule}");

        AssertInlinedUnwrapped(style);
        Assert.Contains(AlsoImported, style);
        Assert.Equal(Green, CascadedColor(style));
    }

    /// <summary>
    /// Cascade 5: an import whose <c>supports()</c> is false must not be fetched. The true import beside
    /// it is the control inside the test: it is requested and inlined, so the server answered. The old
    /// answer requested both and wrapped the false one as <c>@media supports(display: bogus) { … }</c>.
    /// </summary>
    [Fact]
    public void AFalseSupportsImportIsNeitherRequestedNorInlined()
    {
        using var server = new LoopbackStyleServer(new Dictionary<string, string>
        {
            ["/bogus.css"] = "#bogus { color: rgb(255, 0, 0) }",
            ["/grid.css"] = Imported,
        });

        var style = ProjectedStyle(
            $"@import url(/bogus.css) supports(display: bogus); @import url(/grid.css) supports(display: grid); {OwnRule}",
            server.PageUrl);

        Assert.Equal(new[] { "/grid.css" }, server.RequestedPaths());
        Assert.DoesNotContain("#bogus", style);
        Assert.Contains(Imported, style);
        Assert.Contains(OwnRule, style);
        Assert.Equal(Green, CascadedColor(style));
    }

    // ---------------------------------------------------------------------
    //  Media lists, alone and after the other parts
    // ---------------------------------------------------------------------

    /// <summary>
    /// With all three parts, the media query wraps the @layer block (Cascade 5 grammar:
    /// conditions outside, layer inside).
    /// </summary>
    [Fact]
    public void LayerSupportsAndMediaTogetherWrapsMediaAndLayer()
    {
        var style = ProjectedStyle($"@import url({ImportedUrl}) layer(base) supports(display: grid) screen;");

        Assert.Contains("@media screen {", style);
        Assert.Contains("@layer base {", style);
        Assert.DoesNotContain("supports", style);
        Assert.Contains(Imported, style);
        Assert.Equal(Green, CascadedColor(style));
    }

    /// <summary>
    /// Keywords and functions match case-insensitively, and a comment between parts is skipped.
    /// </summary>
    [Fact]
    public void PreludeKeywordsIgnoreCaseAndCommentsBetweenParts()
    {
        var style = ProjectedStyle($"@import url({ImportedUrl}) LAYER(base) /* between */ Supports(display: grid);");

        Assert.Contains("@layer base {", style);
        Assert.Contains(Imported, style);
        Assert.Equal(Green, CascadedColor(style));
    }

    /// <summary>
    /// A media-only import is wrapped in its media rule exactly as before. A control for unchanged
    /// behaviour.
    /// </summary>
    [Fact]
    public void AMediaOnlyImportIsStillWrappedInItsMediaRuleAndApplies()
    {
        var style = ProjectedStyle($"@import url({ImportedUrl}) screen; {OwnRule}");

        Assert.Contains("@media screen {", style);
        Assert.Contains(Imported, style);
        Assert.Equal(Green, CascadedColor(style));
    }

    /// <summary>A <c>print</c> import is wrapped and does not apply on screen. A control.</summary>
    [Fact]
    public void APrintOnlyImportIsWrappedAndDoesNotApply()
    {
        var style = ProjectedStyle($"@import url({ImportedUrl}) print; {OwnRule}");

        Assert.Contains("@media print {", style);
        Assert.Null(CascadedColor(style));
        Assert.Equal("rgb(0, 0, 255)", CascadedColor(style, "own"));
    }

    /// <summary>
    /// Parts out of grammar order are not consumed: <c>supports()</c> is read in its slot, and the
    /// <c>layer(base)</c> after it is left as the media text, which is false, so the sheet does not
    /// apply (Chrome probe L6: <c>media="layer(base)"</c>). The outcome is the same as before; what
    /// changed is where the text goes. The old answer was
    /// <c>@media supports(display: grid) layer(base) { … }</c>.
    /// </summary>
    [Fact]
    public void PartsOutOfGrammarOrderStayInTheMediaTextAndDoNotApply()
    {
        var style = ProjectedStyle($"@import url({ImportedUrl}) supports(display: grid) layer(base); {OwnRule}");

        Assert.Contains("@media layer(base) {", style);
        Assert.Null(CascadedColor(style));
        Assert.Equal("rgb(0, 0, 255)", CascadedColor(style, "own"));
    }

    // ---------------------------------------------------------------------
    //  @layer statements around the imports
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>@layer</c> statement rules may precede the imports (Cascade 5 §2, Chrome probe L11). They are
    /// kept, ahead of the inlined sheet. The old scan stopped at the <c>@layer</c>, so nothing was
    /// inlined and the <c>@import</c> statement stayed in the text.
    /// </summary>
    [Fact]
    public void AnImportAfterLayerStatementsIsInlinedAndTheStatementKept()
    {
        var style = ProjectedStyle($"@layer reset, base; @import url({ImportedUrl}) layer(reset); {OwnRule}");

        Assert.Contains(Imported, style);
        Assert.DoesNotContain("@import", style);
        var statement = style.IndexOf("@layer reset, base;", StringComparison.Ordinal);
        Assert.True(statement >= 0, $"the @layer statement was dropped: {style}");
        Assert.True(
            statement < style.IndexOf(Imported, StringComparison.Ordinal),
            $"the @layer statement is not ahead of the inlined sheet: {style}");
        Assert.True(
            style.IndexOf(Imported, StringComparison.Ordinal) < style.IndexOf(OwnRule, StringComparison.Ordinal),
            $"the inlined sheet is not ahead of the sheet's own rules: {style}");
        Assert.Equal(Green, CascadedColor(style));
        Assert.Equal("rgb(0, 0, 255)", CascadedColor(style, "own"));
    }

    /// <summary>
    /// Once an import has been seen, a <c>@layer</c> statement ends the run: a later <c>@import</c> is
    /// invalid (Cascade 5 §6.4.4.2, Chrome probe L12b) and is neither requested nor inlined. A control:
    /// the old scan also stopped at the <c>@layer</c>; this guards the fix's new tolerance for
    /// <c>@layer</c> statements.
    /// </summary>
    [Fact]
    public void ALayerStatementBetweenImportsEndsTheImportRun()
    {
        using var server = new LoopbackStyleServer(new Dictionary<string, string>
        {
            ["/first.css"] = "#first { color: rgb(0, 0, 41) }",
            ["/second.css"] = "#second { color: rgb(0, 0, 42) }",
        });

        var style = ProjectedStyle(
            $"@import url(/first.css); @layer x; @import url(/second.css); {OwnRule}", server.PageUrl);

        Assert.Equal(new[] { "/first.css" }, server.RequestedPaths());
        Assert.Contains("#first { color: rgb(0, 0, 41) }", style);
        Assert.DoesNotContain("#second", style);
        Assert.Contains(OwnRule, style);
    }

    /// <summary>
    /// An <c>@import</c> with no URL is invalid, and an invalid rule does not count as an import seen: a
    /// <c>@layer</c> statement after it still precedes the imports, and the valid import after that is
    /// inlined (Chromium leaves the allowed rules unchanged for an invalid rule; Chrome 152 applies it).
    /// The old answer stopped at the statement and inlined nothing.
    /// </summary>
    [Fact]
    public void AnInvalidImportBeforeALayerStatementDoesNotEndTheRun()
    {
        var style = ProjectedStyle($"@import foo;\n@layer x;\n@import url({ImportedUrl});\n{OwnRule}");

        Assert.DoesNotContain("@import", style);
        Assert.Contains("@layer x;", style);
        Assert.Equal(Green, CascadedColor(style));
        Assert.Equal(Blue, CascadedColor(style, "own"));
    }

    /// <summary>
    /// <c>@charset</c>, then an <c>@layer</c> statement, then an import: the statement is kept ahead of
    /// the inlined sheet. The old scan stopped at the <c>@layer</c> and inlined nothing.
    /// </summary>
    [Fact]
    public void ACharsetThenALayerStatementThenAnImport()
    {
        var style = ProjectedStyle($"@charset \"utf-8\"; @layer a; @import url({ImportedUrl}) layer(a); {OwnRule}");

        Assert.DoesNotContain("@import", style);
        var statement = style.IndexOf("@layer a;", StringComparison.Ordinal);
        Assert.True(
            statement >= 0 && statement < style.IndexOf(Imported, StringComparison.Ordinal),
            $"the @layer statement is missing or not ahead of the inlined sheet: {style}");
        Assert.Equal(Green, CascadedColor(style));
    }

    // ---------------------------------------------------------------------
    //  Where the run of imports ends
    // ---------------------------------------------------------------------

    /// <summary>
    /// A stylesheet ignores <c>&lt;!--</c> and <c>--&gt;</c> at its top level (css-syntax-3), so the
    /// legacy <c>&lt;style&gt;&lt;!-- @import …; --&gt;</c> still imports, and a <c>--&gt;</c> between two
    /// imports ends nothing. The old scan stopped at either token and inlined neither sheet.
    /// </summary>
    [Fact]
    public void CdoAndCdcAroundAndBetweenImportsAreIgnored()
    {
        var style = ProjectedStyle(
            $"<!--\n@import url({ImportedUrl});\n-->\n@import url({AlsoImportedUrl});\n-->\n{OwnRule}");

        Assert.DoesNotContain("@import", style);
        Assert.Contains(AlsoImported, style);
        Assert.Equal(Green, CascadedColor(style));
        Assert.Equal(Blue, CascadedColor(style, "own"));
    }

    /// <summary>
    /// An unknown at-rule statement is invalid and leaves the imports after it valid (Chrome probe L13),
    /// and a comment may follow the <c>@import</c> keyword directly. The old scan stopped at
    /// <c>@foo;</c>, and did not recognize <c>@import/**/</c> as an import.
    /// </summary>
    [Fact]
    public void AnUnknownAtRuleStatementOrACommentAfterTheKeywordDoesNotEndTheRun()
    {
        var style = ProjectedStyle(
            $"@foo;\n@import url({ImportedUrl});\n@import/**/url({AlsoImportedUrl});\n{OwnRule}");

        Assert.DoesNotContain("@import", style);
        Assert.Contains(AlsoImported, style);
        Assert.Equal(Green, CascadedColor(style));
    }

    /// <summary>
    /// An <c>@import</c> followed by a block is invalid; Chrome 152 drops it and applies the rule after
    /// it. It is left as it is, for the renderer to ignore. The old scan read on to the next <c>;</c>,
    /// inside the following rule, fetched the sheet and spliced that rule's text into the
    /// <c>@media</c> prelude, so <c>#own</c> never applied.
    /// </summary>
    [Fact]
    public void AnImportFollowedByABlockIsNotInlinedAndTheNextRuleSurvives()
    {
        var style = ProjectedStyle($"@import url({ImportedUrl}) screen {{ }}\n#own {{ color: rgb(0, 0, 255); }}");

        Assert.DoesNotContain(Imported, style);
        Assert.Null(CascadedColor(style));
        Assert.Equal(Blue, CascadedColor(style, "own"));
    }

    /// <summary>
    /// A string ends at an unescaped newline (a bad string, css-syntax-3 §4.3.5), which makes that one
    /// import invalid and nothing else. The old scan let the quote run on to the next one, many rules
    /// later, took all of it as the import's URL, requested that, and left the projected sheet empty.
    /// </summary>
    [Fact]
    public void AnUnterminatedStringLosesOnlyItsOwnImport()
    {
        using var server = new LoopbackStyleServer(new Dictionary<string, string>());

        var style = ProjectedStyle($"@import 'x.css\n;\n{OwnRule}\n#p::before {{ content: 'x' }}", server.PageUrl);

        Assert.Empty(server.RequestedPaths());
        Assert.Equal(Blue, CascadedColor(style, "own"));
    }

    // ---------------------------------------------------------------------
    //  URL parsing and the unchanged recursion
    // ---------------------------------------------------------------------

    /// <summary>
    /// A quoted URL ends at its closing quote, not at the first <c>)</c>, in both quote styles. The old
    /// answer cut both at the <c>(</c>'s partner, requested <c>/x(1</c> and <c>/y(2</c>, and inlined
    /// nothing.
    /// </summary>
    [Fact]
    public void AQuotedUrlContainingParenthesesFetchesTheWholeUrl()
    {
        using var server = new LoopbackStyleServer(new Dictionary<string, string>
        {
            ["/x(1).css"] = "#x { color: rgb(0, 0, 51) }",
            ["/y(2).css"] = "#y { color: rgb(0, 0, 52) }",
        });

        var style = ProjectedStyle(
            "@import url(\"x(1).css\"); @import url('y(2).css') screen; " + OwnRule, server.PageUrl);

        Assert.Equal(new[] { "/x(1).css", "/y(2).css" }, server.RequestedPaths());
        Assert.Contains("#x { color: rgb(0, 0, 51) }", style);
        Assert.Contains("@media screen {", style);
        Assert.True(
            style.IndexOf("@media screen {", StringComparison.Ordinal) <
            style.IndexOf("#y { color: rgb(0, 0, 52) }", StringComparison.Ordinal),
            $"y(2).css is not inside its media wrapper: {style}");
        Assert.Contains(OwnRule, style);
    }

    /// <summary>
    /// A string URL ends at its matching quote, and backslash escapes are decoded in both URL forms: an
    /// escaped quote, an escaped <c>)</c>, and a hex escape with the space that ends it. The old answer
    /// ended the string at the escaped quote and <c>url(…)</c> at the escaped <c>)</c>, and kept the
    /// backslashes, so it requested three wrong paths and inlined nothing.
    /// </summary>
    [Fact]
    public void EscapesAreDecodedInBothUrlFormsAndAStringUrlEndsAtItsQuote()
    {
        using var server = new LoopbackStyleServer(new Dictionary<string, string>
        {
            ["/s\"(1).css"] = "#s { color: rgb(0, 0, 53) }",
            ["/u).css"] = "#u { color: rgb(0, 0, 54) }",
            ["/hex.css"] = "#hex { color: rgb(0, 0, 55) }",
        });

        var style = ProjectedStyle(
            "@import \"s\\\"(1).css\" screen; @import url(u\\).css); @import url(h\\65 x.css); " + OwnRule,
            server.PageUrl);

        Assert.Equal(new[] { "/hex.css", "/s\"(1).css", "/u).css" }, server.RequestedPaths());
        Assert.Contains("#s { color: rgb(0, 0, 53) }", style);
        Assert.Contains("#u { color: rgb(0, 0, 54) }", style);
        Assert.Contains("#hex { color: rgb(0, 0, 55) }", style);
        Assert.Contains(OwnRule, style);
    }

    /// <summary>
    /// An unquoted <c>url(</c> holding a quote, an opening parenthesis or inner whitespace is a bad URL
    /// (css-syntax-3 §4.3.6): the import is invalid and a browser requests nothing. The plain URL beside
    /// them, padded with whitespace, is the control inside the test. The old answer requested all three
    /// odd paths.
    /// </summary>
    [Fact]
    public void ABadUnquotedUrlIsNotRequested()
    {
        using var server = new LoopbackStyleServer(new Dictionary<string, string>
        {
            ["/ok.css"] = Imported,
        });

        var style = ProjectedStyle(
            "@import url(/a\"b.css); @import url(/a(b.css); @import url(/a b.css); @import url( /ok.css ); " + OwnRule,
            server.PageUrl);

        Assert.Equal(new[] { "/ok.css" }, server.RequestedPaths());
        Assert.Contains(Imported, style);
        Assert.Contains(OwnRule, style);
    }

    /// <summary>
    /// A plain import whose sheet imports another sheet that imports the first: the nested sheet is
    /// inlined inside its importer and the cycle back is dropped, so each sheet appears once. A
    /// control for the recursion and cycle detection the fix keeps.
    /// </summary>
    [Fact]
    public void ANestedImportCycleStillInlinesEachSheetOnce()
    {
        using var server = new LoopbackStyleServer(new Dictionary<string, string>
        {
            ["/a.css"] = "@import url(b.css); #a { color: rgb(0, 0, 61) }",
            ["/b.css"] = "@import \"a.css\"; #b { color: rgb(0, 0, 62) }",
        });

        var style = ProjectedStyle($"@import url(/a.css); {OwnRule}", server.PageUrl);

        Assert.Equal(new[] { "/a.css", "/b.css" }, server.RequestedPaths());
        Assert.Single(TextRegex.Matches(style, "#a \\{"));
        Assert.Single(TextRegex.Matches(style, "#b \\{"));
        Assert.DoesNotContain("@import", style);
        var b = style.IndexOf("#b {", StringComparison.Ordinal);
        var a = style.IndexOf("#a {", StringComparison.Ordinal);
        var own = style.IndexOf(OwnRule, StringComparison.Ordinal);
        Assert.True(b < a && a < own, $"expected b, then a, then the own rule: {style}");
    }

    /// <summary>
    /// When an import with a named layer fails due to a false supports() condition,
    /// CSS Cascade 5 requires the layer to still be declared: an @layer &lt;name&gt;; statement is emitted.
    /// </summary>
    [Fact]
    public void AFailedNamedLayerImportDueToFalseSupportsEmitsLayerStatement()
    {
        var style = ProjectedStyle($"@import url({ImportedUrl}) layer(foo) supports(display: bogus_invalid); {OwnRule}");

        Assert.Contains("@layer foo;", style);
        Assert.DoesNotContain(Imported, style);
        Assert.Contains(OwnRule, style);
    }

    /// <summary>
    /// When an import cycle occurs for a named-layer import, the recursive import emits
    /// the @layer &lt;name&gt;; statement and breaks the cycle without recursing indefinitely.
    /// </summary>
    [Fact]
    public void AFailedNamedLayerImportCycleEmitsLayerStatement()
    {
        using var server = new LoopbackStyleServer(new Dictionary<string, string>
        {
            ["/a.css"] = "@import url(b.css) layer(foo); #a { color: rgb(0, 0, 61) }",
            ["/b.css"] = "@import \"a.css\" layer(foo); #b { color: rgb(0, 0, 62) }",
        });

        var style = ProjectedStyle($"@import url(/a.css); {OwnRule}", server.PageUrl);

        Assert.Contains("@layer foo;", style);
        Assert.Contains("#a {", style);
        Assert.Contains("#b {", style);
    }

    /// <summary>
    /// Cascade layer order declared via an @layer statement governs the cascade of imported sheets.
    /// Later declared layers win over earlier layers in normal declaration origin.
    /// </summary>
    [Fact]
    public void CascadeLayerOrderGovernsImportedRules()
    {
        var blueRule = "#p { color: rgb(0, 0, 255) }";
        var blueUrl = DataUrl(blueRule);

        // When layer order is "@layer b, a;", layer a wins over layer b.
        var styleA = ProjectedStyle($"@layer b, a; @import url({ImportedUrl}) layer(a); @import url({blueUrl}) layer(b);");
        Assert.Equal(Green, CascadedColor(styleA));

        // When layer order is "@layer a, b;", layer b wins over layer a.
        var styleB = ProjectedStyle($"@layer a, b; @import url({ImportedUrl}) layer(a); @import url({blueUrl}) layer(b);");
        Assert.Equal(Blue, CascadedColor(styleB));
    }

    /// <summary>
    /// Normal declarations outside of any layer beat normal declarations inside a layer,
    /// regardless of specificity (CSS Cascade 5 §6.4).
    /// </summary>
    [Fact]
    public void UnlayeredRuleBeatsLayeredImportedRuleRegardlessOfSpecificity()
    {
        var highSpecGreen = DataUrl("#p.special { color: rgb(0, 128, 0) }");
        var lowSpecBlue = "p { color: rgb(0, 0, 255); }";

        var style = ProjectedStyle($"@import url({highSpecGreen}) layer(base); {lowSpecBlue}");
        Assert.Equal(Blue, CascadedColor(style));
    }

    /// <summary>
    /// Verifies that getComputedStyle() in script resolves rules from @import statements
    /// via BridgeStyleSheetLoader and CssStyleScopeBuilder.
    /// </summary>
    [Fact]
    public void GetComputedStyleResolvesRulesFromImportedStyleSheet()
    {
        var html = @"<!DOCTYPE html><html><head>
        <style id=""s"">
          @import url('" + ImportedUrl + @"');
        </style>
        </head><body><p id=""p"">test</p><div id=""out""></div></body></html>";

        var executed = new ScriptEngine().Execute([
            "document.getElementById('out').textContent = getComputedStyle(document.getElementById('p')).color;"
        ], html, PageUrl);

        var match = TextRegex.Match(executed!, @"<div id=""out"">(.*?)</div>");
        Assert.True(match.Success);
        Assert.Equal(Green, match.Groups[1].Value);
    }

    /// <summary>
    /// Verifies that getComputedStyle() respects cascade layers from imported stylesheets.
    /// </summary>
    [Fact]
    public void GetComputedStyleRespectsCascadeLayersInImports()
    {
        var blueUrl = DataUrl("#p { color: rgb(0, 0, 255); }");
        var html = @"<!DOCTYPE html><html><head>
        <style id=""s"">
          @layer a, b;
          @import url('" + ImportedUrl + @"') layer(a);
          @import url('" + blueUrl + @"') layer(b);
        </style>
        </head><body><p id=""p"">test</p><div id=""out""></div></body></html>";

        var executed = new ScriptEngine().Execute([
            "document.getElementById('out').textContent = getComputedStyle(document.getElementById('p')).color;"
        ], html, PageUrl);

        var match = TextRegex.Match(executed!, @"<div id=""out"">(.*?)</div>");
        Assert.True(match.Success);
        Assert.Equal(Blue, match.Groups[1].Value);
    }
}

