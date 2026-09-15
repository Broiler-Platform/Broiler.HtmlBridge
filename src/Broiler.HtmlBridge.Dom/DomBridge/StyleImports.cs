using System.Text;
using Broiler.Dom;
using Broiler.HtmlBridge.Internal.Scripting;
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
                var expanded = ExpandCssImports(
                    original, documentBaseUrl(), new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);
                if (!string.Equals(expanded, original, StringComparison.Ordinal))
                    SetElementTextContent(element, expanded);
            }
        }

        foreach (var child in ChildElements(element))
            InlineStyleSheetImports(child, documentBaseUrl);
    }

    /// <summary>
    /// Replaces the leading run of <c>@import</c> statements in <paramref name="css"/> with
    /// the fetched (and recursively expanded) text of each imported sheet, resolved against
    /// <paramref name="baseUrl"/>. <paramref name="chain"/> tracks the current import chain
    /// so a cycle (A→B→A) resolves to nothing instead of recursing forever; it is a
    /// per-chain set (an already-resolved URL is removed after its subtree expands) so a
    /// diamond import still inlines the shared sheet on each independent path.
    /// </summary>
    private string ExpandCssImports(string css, string baseUrl, HashSet<string> chain, int depth)
    {
        var (endOffset, imports) = ScanLeadingImports(css);
        if (imports.Count == 0 || depth >= MaxImportDepth)
            return css;

        var sb = new StringBuilder();
        foreach (var (href, media) in imports)
        {
            if (string.IsNullOrEmpty(href))
                continue;

            var absolute = ResolveStyleSheetUrl(href, baseUrl);
            if (absolute is null || !chain.Add(absolute))
                continue; // unresolvable, or already on the current chain (cycle)

            try
            {
                var imported = FetchStyleSheetText(absolute);
                if (!string.IsNullOrEmpty(imported))
                {
                    var rebased = RebaseRelativeUrls(imported, absolute);
                    var nested = ExpandCssImports(rebased, absolute, chain, depth + 1);
                    if (!string.IsNullOrWhiteSpace(media))
                        sb.Append("@media ").Append(media).Append(" {\n").Append(nested).Append("\n}\n");
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
