using System.Text;
using Broiler.CSS;
using Broiler.CSS.Dom;
using Broiler.Dom;
using Broiler.HtmlBridge.Internal.Scripting;
using Broiler.HtmlBridge.Scripting;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// Render-time <c>@import</c> resolution. The renderer re-parses the serialized HTML and
/// applies each <c>&lt;style&gt;</c>/<c>&lt;link&gt;</c> sheet, but it does not fetch a
/// sheet's <c>@import</c> rules — an <c>@import</c> statement is parsed and then ignored,
/// so the imported rules never reach the cascade (the WPT <c>css/cssom</c> import family,
/// e.g. <c>cssimportrule-parent</c>, rendered blank where the import set a background).
/// This transform inlines the imported CSS text into the importing <c>&lt;style&gt;</c>
/// during the render-bound serialization, so the renderer sees the imported rules.
/// <para>
/// Runs inside <see cref="ApplySerializationTransforms"/>, so it only affects the
/// render-bound document — JS-visible <c>innerHTML</c>/<c>outerHTML</c> (which serialize
/// without the transforms) still expose the author <c>@import</c> statement, and the
/// live CSSOM rule model (<c>cssRules</c>) is untouched. Only <c>&lt;style&gt;</c>
/// elements are inlined: a linked sheet's imports would need their relative <c>url()</c>s
/// re-based onto the link (not the document), the same limitation
/// <see cref="ApplyCssomStyleSheetMutations"/> notes for baking linked sheets.
/// </para>
/// <para>
/// <b>Imported rules never reach <c>getComputedStyle</c>.</b> They exist only in this projection:
/// <see cref="GetSyncedScopedEngine"/> feeds the engine each sheet's own text, and neither Broiler.CSS
/// nor Broiler.CSS.Dom resolves <c>@import</c>, so paint and computed style disagree about every
/// imported rule whatever is emitted here.
/// </para>
/// <para>
/// <b>Content Security Policy.</b> An import is a stylesheet request like a <c>&lt;link&gt;</c>'s
/// (CSS Cascade 4 "fetch an <c>@import</c>" runs css-values-4 "fetch a style resource" with destination
/// <c>style</c>, and CSP3 §6.1.13 names <c>@import</c>), so each one — <c>data:</c> included, at every
/// nesting depth — passes the <c>style-src-elem</c> → <c>style-src</c> → <c>default-src</c> check a
/// <c>&lt;link&gt;</c> passes, against its URL resolved against the importing sheet, before anything is
/// fetched or decoded. A blocked import contributes nothing; the imports after it and the importing
/// sheet's own rules still apply. The one exemption is a <c>&lt;meta&gt;</c>-delivered policy's: the
/// imports a <c>&lt;style&gt;</c> held when the parser read it ahead of the meta, as the Attach-time
/// gate recorded them (<see cref="RecordImportsBeforePolicyMeta"/>). See <see cref="ExpandCssImports"/>
/// for the nonce and for why that record, not this projection's tree, decides.
/// </para>
/// <para>
/// <b>Import conditions.</b> The prelude is split along the Cascade 5 grammar
/// (<see cref="DomBridgeUtils.ScanLeadingImports"/>). A false <c>supports()</c> skips the import, a
/// true one leaves no wrapper, a media list becomes an <c>@media</c> wrapper, and a <c>layer</c> is
/// inlined unlayered because the consumed cascade discards layered rules; the emitter says why for each.
/// </para>
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>
    /// Inlines the leading <c>@import</c> rules of every <c>&lt;style&gt;</c> in the tree,
    /// replacing each with the (recursively import-expanded) text of the imported sheet.
    /// </summary>
    private void InlineStyleSheetImports(DomElement root)
    {
        // HTML §4.2.3: <base href> replaces the document URL that a relative
        // @import resolves against, so the base has to be folded in rather than
        // resolving each import against the page URL. (This transform runs before
        // ApplyBaseHrefToStyleUrls, so it cannot rely on that pass.)
        //
        // Resolved LAZILY, and at most once: finding the base means walking every
        // descendant, and the overwhelming majority of documents have no @import
        // at all — they must not pay for a second full-tree scan. A document with
        // thousands of nodes and no imports is the case that makes this matter.
        string? documentBaseUrl = null;
        string ResolveBaseOnce() =>
            documentBaseUrl ??= HtmlBaseHref.ResolveDocumentBaseUrl(
                _pageUrl,
                TryFindDocumentBaseHref(root, out var baseHref) ? baseHref : null);

        InlineStyleSheetImports(root, ResolveBaseOnce);
    }

    private void InlineStyleSheetImports(DomElement element, Func<string> documentBaseUrl)
    {
        if (!IsText(element) &&
            element.TagName.Equals("style", StringComparison.OrdinalIgnoreCase))
        {
            var original = GetStyleElementCssText(element);
            if (HasLeadingImport(original))
            {
                // The record is keyed by the live element; this one is its copy in the projection.
                var exempt = _importsBeforePolicyMeta?.GetValueOrDefault(ResolveRenderSource(element));
                var expanded = ExpandCssImports(
                    original, documentBaseUrl(), new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, exempt);
                if (!string.Equals(expanded, original, StringComparison.Ordinal))
                    element.TextContent = expanded;
            }
        }

        foreach (var child in ChildElements(element))
            InlineStyleSheetImports(child, documentBaseUrl);
    }

    /// <summary>
    /// For each <c>&lt;style&gt;</c> the Attach-time gate reached before a <c>&lt;meta&gt;</c>-delivered
    /// policy's meta, the absolute URLs of the imports it held then, keyed by the element's identity;
    /// <see langword="null"/> when there are none (no policy, a header policy, or no such block with an
    /// import). Rebuilt by every run of <see cref="ApplyStyleContentSecurityPolicy"/>.
    /// </summary>
    private Dictionary<DomElement, HashSet<string>>? _importsBeforePolicyMeta;

    /// <summary>
    /// Records the leading imports of <paramref name="element"/>, when it is a <c>&lt;style&gt;</c> the
    /// Attach-time gate (<see cref="ApplyStyleCsp"/>) reached before the policy's meta, as requests a
    /// browser made before the policy took effect. Each URL is resolved as the projection resolves it,
    /// against the document base, so an unchanged block yields the same URLs there, and a
    /// <c>&lt;base&gt;</c> that script adds later points them at URLs this record does not hold.
    /// </summary>
    private void RecordImportsBeforePolicyMeta(DomElement element)
    {
        if (IsText(element) || !element.TagName.Equals("style", StringComparison.OrdinalIgnoreCase))
            return;

        var css = GetStyleElementCssText(element);
        if (!HasLeadingImport(css))
            return;

        var urls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var import in ScanLeadingImports(css).Imports)
        {
            if (import.Href.Length > 0 && ResolveStyleSheetUrl(import.Href, DocumentBaseUrl()) is { } absolute)
                urls.Add(absolute);
        }

        (_importsBeforePolicyMeta ??= new(ReferenceEqualityComparer.Instance))[element] = urls;
    }

    /// <summary>
    /// Replaces the leading run of <c>@import</c> statements in <paramref name="css"/> with
    /// the fetched (and recursively expanded) text of each imported sheet, resolved against
    /// <paramref name="baseUrl"/>. <paramref name="chain"/> tracks the current import chain
    /// so a cycle (A→B→A) resolves to nothing instead of recursing forever; it is a
    /// per-chain set (an already-resolved URL is removed after its subtree expands) so a
    /// diamond import still inlines the shared sheet on each independent path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The policy check.</b> Each import is checked with <see cref="IsStyleFetchAllowedByCsp"/>,
    /// the request-level check a <c>&lt;link rel="stylesheet"&gt;</c> goes through, against its resolved
    /// absolute URL with the page URL as <c>'self'</c>, and always with no nonce. css-values-4 "fetch a
    /// style resource" never sets the request's cryptographic nonce metadata, and CSP3 matches an empty
    /// nonce against nothing, so a <c>&lt;style nonce&gt;</c>'s nonce authorizes the block and not its
    /// imports (Chrome 152 blocks and never requests them, probes C1 and C9). <c>'unsafe-inline'</c>
    /// authorizes no URL either: <see cref="ContentSecurityPolicy.AllowsExternalStyle"/> does not read it.
    /// A blocked import is dropped silently, as a blocked <c>&lt;link&gt;</c> is: this runs on every
    /// projection, and a warning per projection would be noise.
    /// </para>
    /// <para>
    /// <b>A <c>&lt;meta&gt;</c> policy is not retroactive.</b> <paramref name="exemptUrls"/> holds the
    /// imports a <c>&lt;style&gt;</c> held when the parser read it ahead of the policy's meta: a browser
    /// requested those before the meta took effect, and the Attach-time gate kept the block for the same
    /// reason. An import whose resolved URL is in it is not checked. It is what that gate recorded, and not
    /// something worked out from the tree being projected here, because page script has run in between:
    /// a meta script inserts, moves or removes, a block it inserts before the meta, or an import it
    /// writes into an exempt block would otherwise decide what the policy binds. In a browser none of
    /// that changes the policy list a request is checked against (CSP3 §3.3), and a meta can only add a
    /// policy, never lift a header's. The sheets the exempt imports bring in are checked (nested calls
    /// pass <see langword="null"/>): their requests go out when that sheet arrives, which is normally
    /// after the parser has passed the meta, and checking them is the answer that fails closed.
    /// </para>
    /// </remarks>
    private string ExpandCssImports(
        string css, string baseUrl, HashSet<string> chain, int depth, HashSet<string>? exemptUrls)
    {
        var (endOffset, layerStatements, imports) = ScanLeadingImports(css);
        if (imports.Count == 0 || depth >= MaxImportDepth)
            return css;

        var sb = new StringBuilder();

        // The @layer statement rules the scan let through ahead of the imports stay ahead of what
        // replaces them, in order. The consumed engine ignores layer order today (see below), but
        // dropping the statements would lose it for good.
        foreach (var statement in layerStatements)
            sb.Append(statement).Append('\n');

        foreach (var import in imports)
        {
            if (string.IsNullOrEmpty(import.Href))
                continue;

            // supports(X) is decided here, before the policy check and before any request: Cascade 5
            // says an import whose supports() is false must not be fetched, so it has no request for
            // the policy to judge either. "(X)" covers both the <declaration> and the
            // <supports-condition> forms, and it is the evaluator CSS.supports() uses. Comments are
            // removed first, with the CssSyntax.RemoveComments that CssParser reads an @supports
            // prelude with (the evaluator answers false for text that still holds one), so this is the
            // answer the renderer gives "@supports (X)". A true one emits no @supports wrapper: the
            // renderer cascades with the same Broiler.CSS.Dom, so the answer cannot differ there, and a
            // wrapper would hide the imported sheet's @keyframes from Broiler.Layout's animation
            // resolver, which reads only top-level @keyframes (probes x6/x7).
            if (import.Supports is not null &&
                !CssStyleEngine.EvaluatesSupportsCondition("(" + CssSyntax.RemoveComments(import.Supports) + ")"))
                continue;

            var absolute = ResolveStyleSheetUrl(import.Href, baseUrl);
            if (absolute is null)
                continue; // unresolvable

            if (exemptUrls?.Contains(absolute) != true && !IsStyleFetchAllowedByCsp(absolute, nonce: null))
                continue; // refused before any request or data: decode; see the remarks

            if (!chain.Add(absolute))
                continue; // already on the current chain (cycle)

            try
            {
                var imported = FetchStyleSheetText(absolute);
                if (!string.IsNullOrEmpty(imported))
                {
                    var rebased = RebaseRelativeUrls(imported, absolute);
                    var nested = ExpandCssImports(rebased, absolute, chain, depth + 1, exemptUrls: null);

                    // layer / layer(name) emits NO @layer wrapper. The consumed Broiler.CSS.Dom, which
                    // cascades both for the renderer (through Broiler.HTML) and for this bridge, has no
                    // @layer case in either cascade walker and discards every rule inside an @layer block
                    // (probes a, b, g, k14-k16; StyleSheetImportConditionTests.
                    // TheConsumedCascadeStillDiscardsRulesInsideLayerBlocks fails the day that changes),
                    // so "@layer base { ... }" would keep the imported rules as invisible as the old
                    // "@media layer(base) { ... }" did. The sheet is inlined unlayered at the import's
                    // position instead: its rules apply, and the importing sheet's later rules still win
                    // at equal or higher specificity, which is the common reset/base-layer outcome. What
                    // that cannot express:
                    //  - a later unlayered rule beating a layered rule of higher specificity (unlayered
                    //    author rules outrank every layer);
                    //  - an EARLIER unlayered rule (an earlier <style> or <link>, or an earlier rule in
                    //    this sheet) beating a layered one: flattened, the imported rule now comes later
                    //    and wins whenever specificity is equal;
                    //  - a layered !important beating an unlayered !important;
                    //  - layer order, whether declared by @layer statements (kept above, ignored by the
                    //    engine) or between named layers;
                    //  - an anonymous layer's identity (each bare `layer` is a layer of its own);
                    //  - merging with an @layer block of the same name elsewhere, which the engine drops,
                    //    so only the imported half applies;
                    //  - a named-layer import that fails or is blocked still declaring its layer.
                    // Once Broiler.CSS.Dom implements layers, switch to the Cascade 5 equivalent, conditions
                    // outside and layer inside: "@media M { @layer name { ... } }", "@layer { ... }" for
                    // the bare keyword, and "@layer name;" in the same place for a failed named import.
                    if (import.Media.Length > 0)
                        sb.Append("@media ").Append(import.Media).Append(" {\n").Append(nested).Append("\n}\n");
                    else
                        sb.Append(nested).Append('\n');
                }
            }
            finally
            {
                chain.Remove(absolute);
            }
        }

        sb.Append(css, endOffset, css.Length - endOffset);
        return sb.ToString();
    }

    /// <summary>Resolves a stylesheet href against a base URL. A <c>data:</c> href is its
    /// own absolute URL; everything else goes through the shared URL resolver.</summary>
    private string? ResolveStyleSheetUrl(string href, string baseUrl)
    {
        if (href.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return href;
        return UrlResolver.Resolve(href, string.IsNullOrEmpty(baseUrl) ? _pageUrl : baseUrl)?.AbsoluteUri;
    }

    /// <summary>
    /// Fetches the CSS text for a stylesheet URL — decoding a <c>data:</c> URI in-process, or
    /// loading <c>file</c>/<c>http(s)</c> via the loader. The seam every stylesheet read goes
    /// through, <c>@import</c> and <c>&lt;link rel="stylesheet"&gt;</c> alike, so a <c>data:</c>
    /// sheet is decoded wherever it is named rather than only where someone remembered to.
    /// Anything but a <c>data:</c> URI is passed to the loader as given, which for a
    /// <c>&lt;link&gt;</c> href means the caller resolves it first if it needs to be absolute.
    /// <para>
    /// It applies no Content Security Policy: the <c>style-src</c> gate is its callers' job, done
    /// before they call it (<see cref="IsExternalStyleAllowedByCsp"/> on the <c>&lt;link&gt;</c> paths,
    /// <see cref="IsStyleFetchAllowedByCsp"/> in <see cref="ExpandCssImports"/>), so a new caller has
    /// to check first — a <c>data:</c> URI is a request the policy governs too.
    /// </para>
    /// </summary>
    private string? FetchStyleSheetText(string url)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var (_, body) = DecodeDataUriParts(url);
            return body;
        }
        return FetchExternalStylesheet(url);
    }
}
