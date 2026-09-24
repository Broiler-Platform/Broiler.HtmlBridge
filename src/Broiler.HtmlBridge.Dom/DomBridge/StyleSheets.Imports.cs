using System.Text;
using Broiler.CSS;
using Broiler.CSS.Cssom;
using Broiler.CSS.Dom;
using Broiler.Dom;
using Broiler.HtmlBridge.Internal.Scripting;
using Broiler.HtmlBridge.Scripting;
using static Broiler.HtmlBridge.DomBridgeUtils;
using Broiler.Net.Http;

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
/// <b>Imported rules reach both <c>getComputedStyle</c> and the renderer.</b>
/// <see cref="GetSyncedScopedEngine"/> passes <see cref="BridgeStyleSheetLoader"/>
/// and each sheet's base URL to <see cref="CssStyleScopeBuilder"/>, which expands
/// <c>@import</c> rules via <see cref="CssImportResolver"/>, while this projection
/// inlines them into the render-bound HTML serialization.
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
/// (<see cref="ScanLeadingImports"/>). A false <c>supports()</c> skips the import, a
/// true one leaves no wrapper, a media list becomes an <c>@media</c> wrapper, and a <c>layer</c> is
/// inlined in its layer; the emitter says why for each.
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

        // The imports are the requests of the document the projection shows: the live document the
        // projected root stands for, a frame's included.
        InlineStyleSheetImports(root, ResolveBaseOnce, ResolveRenderSource(root));
    }

    private void InlineStyleSheetImports(DomElement element, Func<string> documentBaseUrl, DomNode importer)
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
                    original, documentBaseUrl(), new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, exempt, importer);
                if (!string.Equals(expanded, original, StringComparison.Ordinal))
                    element.TextContent = expanded;
            }
        }

        foreach (var child in ChildElements(element))
            InlineStyleSheetImports(child, documentBaseUrl, importer);
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
    /// <param name="importer">A live node of the importing document, whose requests the imports are.</param>
    private string ExpandCssImports(
        string css, string baseUrl, HashSet<string> chain, int depth, HashSet<string>? exemptUrls, DomNode importer)
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
            {
                EmitFailedNamedLayer(sb, import);
                continue;
            }

            var absolute = ResolveStyleSheetUrl(import.Href, baseUrl);
            if (absolute is null)
            {
                EmitFailedNamedLayer(sb, import);
                continue; // unresolvable
            }

            if (exemptUrls?.Contains(absolute) != true && !IsStyleFetchAllowedByCsp(absolute, nonce: null))
            {
                EmitFailedNamedLayer(sb, import);
                continue; // refused before any request or data: decode; see the remarks
            }

            if (!chain.Add(absolute))
            {
                EmitFailedNamedLayer(sb, import);
                continue; // already on the current chain (cycle)
            }

            try
            {
                var imported = FetchStyleSheetText(
                    absolute, ImportStyleSheetRequest(importer, exemptFromCsp: exemptUrls?.Contains(absolute) == true));
                if (!string.IsNullOrEmpty(imported))
                {
                    var rebased = RebaseRelativeUrls(imported, absolute);
                    var nested = ExpandCssImports(rebased, absolute, chain, depth + 1, exemptUrls: null, importer);

                    var content = nested;
                    if (import.Layer == CssImportLayer.Named && import.LayerName is not null)
                        content = $"@layer {import.LayerName} {{\n{nested}\n}}";
                    else if (import.Layer == CssImportLayer.Anonymous)
                        content = $"@layer {{\n{nested}\n}}";

                    if (import.Media.Length > 0)
                        sb.Append("@media ").Append(import.Media).Append(" {\n").Append(content).Append("\n}\n");
                    else
                        sb.Append(content).Append('\n');
                }
                else
                {
                    EmitFailedNamedLayer(sb, import);
                }
            }
            finally
            {
                chain.Remove(absolute);
            }
        }

        sb.Append(css, endOffset, css.Length - endOffset);
        return sb.ToString();

        static void EmitFailedNamedLayer(StringBuilder sb, CssImportMetadata import)
        {
            if (import.Layer == CssImportLayer.Named && import.LayerName is not null)
                sb.Append("@layer ").Append(import.LayerName).Append(";\n");
        }
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
    /// <para>
    /// <paramref name="request"/> is how the requesting document sends the request — a link's
    /// (<see cref="LinkStyleSheetRequest"/>) or an import's (<see cref="ImportStyleSheetRequest"/>) —
    /// and carries the same policy check as its host policy, so a redirect to a URL the policy
    /// forbids is refused before that URL is fetched, and the document's mode, which decides whether
    /// a response that is not <c>text/css</c> may still apply.
    /// </para>
    /// </summary>
    private string? FetchStyleSheetText(string url, Dom.Runtime.StyleSheetRequest request)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var (_, body) = DecodeDataUriParts(url);
            return body;
        }
        return FetchExternalStylesheet(url, request);
    }

    /// <summary>
    /// Whether <paramref name="absoluteUrl"/> is among the leading imports of a <c>&lt;style&gt;</c>
    /// element reached before a <c>&lt;meta&gt;</c>-delivered CSP policy took effect.
    /// </summary>
    internal bool IsImportExemptFromCsp(string absoluteUrl)
    {
        if (_importsBeforePolicyMeta is null)
            return false;

        foreach (var set in _importsBeforePolicyMeta.Values)
        {
            if (set.Contains(absoluteUrl))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Implements <see cref="ICssStyleSheetLoader"/> for computed-style engine scope assembly.
    /// Resolves <c>@import</c> URLs against referrer/document base URL, verifies CSP, fetches
    /// stylesheet text, and rebases relative URLs.
    /// </summary>
    /// <remarks>
    /// One per computed-style scope, because the imports are the requests of the scope's document:
    /// a frame's, for a frame's scope. A loader shared by every scope sent a cross-site frame's
    /// imports as the top document's, with its <c>SameSite</c> cookies.
    /// </remarks>
    /// <param name="bridge">The bridge the scope belongs to.</param>
    /// <param name="scopeRoot">The root of the scope's document, asked for its request context and mode at each load.</param>
    private sealed class BridgeStyleSheetLoader(DomBridge bridge, DomNode scopeRoot) : ICssStyleSheetLoader
    {
        public string? LoadStyleSheet(string href, string? referrerUrl = null)
        {
            if (string.IsNullOrWhiteSpace(href))
                return null;

            var baseUrl = !string.IsNullOrWhiteSpace(referrerUrl) ? referrerUrl : bridge.DocumentBaseUrl();
            var absolute = bridge.ResolveStyleSheetUrl(href, baseUrl);
            if (absolute is null)
                return null;

            var exempt = bridge.IsImportExemptFromCsp(absolute);
            if (!exempt && !bridge.IsStyleFetchAllowedByCsp(absolute, nonce: null))
                return null;

            var text = bridge.FetchStyleSheetText(absolute, bridge.ImportStyleSheetRequest(scopeRoot, exempt));
            if (string.IsNullOrEmpty(text))
                return text;

            return RebaseRelativeUrls(text, absolute);
        }
    }

    /// <summary>
    /// Whether <paramref name="css"/> begins with at least one <c>@import</c> that
    /// <see cref="ScanLeadingImports"/> would collect, so a sheet with none skips the expansion.
    /// </summary>
    private static bool HasLeadingImport(string css)
        => css.Contains("@import", StringComparison.OrdinalIgnoreCase) &&
           ScanLeadingImports(css).Imports.Count > 0;

    /// <summary>
    /// Scans the leading portion of a stylesheet, where <c>@import</c> rules are valid, collecting each
    /// one's parsed prelude via <see cref="CssomRuleMetadata.ParseImportPrelude"/> and the offset at which
    /// the first other content begins.
    /// </summary>
    internal static (int EndOffset, List<string> LayerStatements, List<CssImportMetadata> Imports) ScanLeadingImports(string css)
    {
        var layerStatements = new List<string>();
        var imports = new List<CssImportMetadata>();
        var sawValidImport = false;
        var i = 0;
        var n = css.Length;
        var consumedEnd = 0;

        while (i < n)
        {
            while (i < n && char.IsWhiteSpace(css[i]))
                i++;
            if (i == n)
                break;

            if (i + 1 < n && css[i] == '/' && css[i + 1] == '*')
            {
                i = SkipComment(css, i);
                continue;
            }

            if (string.CompareOrdinal(css, i, "<!--", 0, 4) == 0 || string.CompareOrdinal(css, i, "-->", 0, 3) == 0)
            {
                i += css[i] == '<' ? 4 : 3;
                consumedEnd = i;
                continue;
            }

            if (StartsWithAtKeyword(css, i, "@import"))
            {
                var stmtEnd = ScanStatement(css, i, out var opensBlock);
                if (opensBlock)
                    break;

                var prelude = css[(i + "@import".Length)..stmtEnd].Trim().TrimEnd(';').Trim();
                var import = ParseImportPrelude(prelude);
                imports.Add(import);
                sawValidImport |= import.Href.Length > 0;
                i = stmtEnd;
                consumedEnd = i;
                continue;
            }

            if (StartsWithAtKeyword(css, i, "@layer"))
            {
                var stmtEnd = ScanStatement(css, i, out var opensBlock);
                if (opensBlock || sawValidImport)
                    break;

                layerStatements.Add(css[i..stmtEnd]);
                i = stmtEnd;
                consumedEnd = i;
                continue;
            }

            if (i + 1 < n && css[i] == '@' && (IsNameStartChar(css[i + 1]) || css[i + 1] == '-') &&
                !StartsWithAtKeyword(css, i, "@namespace"))
            {
                var stmtEnd = ScanStatement(css, i, out var opensBlock);
                if (opensBlock)
                    break;

                i = stmtEnd;
                consumedEnd = i;
                continue;
            }

            break;
        }

        return (consumedEnd, layerStatements, imports);
    }

    private static bool StartsWithAtKeyword(string css, int pos, string keyword)
    {
        if (pos + keyword.Length > css.Length)
            return false;
        if (string.Compare(css, pos, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
            return false;
        var after = pos + keyword.Length;
        if (after == css.Length)
            return true;
        var next = css[after];
        return char.IsWhiteSpace(next) || next == '"' || next == '\'' || next == '(' || next == ';' ||
               (next == '/' && after + 1 < css.Length && css[after + 1] == '*');
    }

    private static int ScanStatement(string css, int start, out bool opensBlock)
    {
        opensBlock = false;
        var i = start;
        var n = css.Length;
        var depth = 0;
        while (i < n)
        {
            var c = css[i];
            if (c == '"' || c == '\'')
            {
                i = SkipString(css, i);
            }
            else if (c == '/' && i + 1 < n && css[i + 1] == '*')
            {
                i = SkipComment(css, i);
            }
            else if (c == '\\')
            {
                i = Math.Min(n, i + 2);
            }
            else if ((c == 'u' || c == 'U') && IsUnquotedUrlStart(css, i, out var bodyStart))
            {
                i = SkipUnquotedUrlBody(css, bodyStart);
            }
            else
            {
                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    if (depth > 0)
                        depth--;
                }
                else if (depth == 0 && c == ';')
                {
                    return i + 1;
                }
                else if (depth == 0 && c == '{' && !opensBlock)
                {
                    opensBlock = true;
                }

                i++;
            }
        }
        return n;
    }

    private static bool IsUnquotedUrlStart(string text, int i, out int bodyStart)
    {
        bodyStart = i + 4;
        if ((i > 0 && (IsNameChar(text[i - 1]) || text[i - 1] == '\\')) ||
            string.Compare(text, i, "url(", 0, 4, StringComparison.OrdinalIgnoreCase) != 0)
            return false;

        var j = bodyStart;
        while (j < text.Length && IsCssWhitespace(text[j]))
            j++;
        return j >= text.Length || (text[j] != '"' && text[j] != '\'');
    }

    private static int SkipUnquotedUrlBody(string text, int i)
    {
        while (i < text.Length && text[i] != ')')
            i += text[i] == '\\' ? 2 : 1;
        return Math.Min(text.Length, i + 1);
    }

    private static int SkipString(string text, int i)
    {
        var quote = text[i++];
        while (i < text.Length && text[i] != quote)
        {
            if (text[i] is '\n' or '\r' or '\f')
                return i;
            i += text[i] == '\\' ? 2 : 1;
        }
        return Math.Min(text.Length, i + 1);
    }

    private static int SkipComment(string text, int i)
    {
        i += 2;
        while (i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/'))
            i++;
        return Math.Min(text.Length, i + 2);
    }

    private static bool IsNameStartChar(char c) => char.IsAsciiLetter(c) || c == '_' || c >= 0x80;
    private static bool IsNameChar(char c) => IsNameStartChar(c) || char.IsAsciiDigit(c) || c == '-';
    private static bool IsCssWhitespace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

    private static int SkipComments(string text, int i)
    {
        while (i + 1 < text.Length && text[i] == '/' && text[i + 1] == '*')
            i = SkipComment(text, i);
        return i;
    }

    private static int SkipWhitespaceAndComments(string text, int i)
    {
        while (i < text.Length)
        {
            if (IsCssWhitespace(text[i]))
                i++;
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
                i = SkipComment(text, i);
            else
                break;
        }
        return i;
    }

    private static CssImportMetadata ParseImportPrelude(string prelude)
    {
        var meta = CssomRuleMetadata.ParseImportPrelude(prelude);
        if (meta.Layer == CssImportLayer.Named)
        {
            var layerIdx = prelude.IndexOf("layer(", StringComparison.OrdinalIgnoreCase);
            if (layerIdx >= 0)
            {
                var start = layerIdx + "layer(".Length;
                var close = prelude.IndexOf(')', start);
                if (close >= 0)
                {
                    if (!TryReadLayerName(prelude, start, close, out var validName))
                    {
                        return new CssImportMetadata(meta.Href, CssImportLayer.None, null, meta.Supports, prelude[layerIdx..].Trim());
                    }

                    if (!string.Equals(validName, meta.LayerName, StringComparison.Ordinal))
                    {
                        meta = new CssImportMetadata(meta.Href, CssImportLayer.Named, validName, meta.Supports, meta.Media);
                    }
                }
            }
        }
        return meta;
    }

    private static bool TryReadLayerName(string text, int start, int end, out string name)
    {
        name = string.Empty;
        var written = new StringBuilder();
        var i = SkipWhitespaceAndComments(text, start);
        while (true)
        {
            var segmentStart = i;
            if (!TryConsumeIdent(text, ref i))
                return false;

            written.Append(text, segmentStart, i - segmentStart);
            i = SkipComments(text, i);
            if (i >= end || text[i] != '.')
                break;

            written.Append('.');
            i = SkipComments(text, i + 1);
        }

        if (SkipWhitespaceAndComments(text, i) != end)
            return false;

        name = written.ToString();
        return true;
    }

    private static bool TryConsumeIdent(string text, ref int i)
    {
        var j = i;
        if (j < text.Length && text[j] == '-')
        {
            j++;
            if (j < text.Length && text[j] == '-')
                j++;
            else if (!StartsName(text, j))
                return false;
        }
        else if (!StartsName(text, j))
        {
            return false;
        }

        while (j < text.Length)
        {
            if (CssSyntax.IsValidEscape(text, j))
                SkipCssEscape(text, ref j);
            else if (IsNameChar(text[j]))
                j++;
            else
                break;
        }

        i = j;
        return true;

        static bool StartsName(string s, int k) =>
            k < s.Length && (IsNameStartChar(s[k]) || CssSyntax.IsValidEscape(s, k));
    }

    /// <summary>Advances <paramref name="i"/> past one CSS escape sequence (CSS Syntax 3
    /// §4.3.7). Skip-only: the decoded code point has no consumer here — prelude values are
    /// decoded by <c>CssomRuleMetadata.ParseImportPrelude</c>.</summary>
    private static void SkipCssEscape(string text, ref int i)
    {
        i++;
        if (i >= text.Length)
            return;

        if (char.IsAsciiHexDigit(text[i]))
        {
            var digitsEnd = Math.Min(text.Length, i + 6);
            while (i < digitsEnd && char.IsAsciiHexDigit(text[i]))
                i++;

            if (i < text.Length && IsCssWhitespace(text[i]))
                i += text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
            return;
        }

        if (text[i] is '\n' or '\r' or '\f')
        {
            i += text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
            return;
        }

        i++;
    }
}
