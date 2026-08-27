using System.Linq;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// Render-time <c>&lt;base href&gt;</c> resolution for stylesheet <c>url()</c> references.
/// The renderer resolves a relative CSS <c>url()</c> against a single document base URL and
/// never consults the <c>&lt;base&gt;</c> element, so a page that sets
/// <c>&lt;base href="/images/"&gt;</c> and then references <c>url(green.png)</c> resolved the
/// image against the document's own directory instead of the base — the resource was not
/// found and the fallback colour showed through (the WPT <c>css/…/*base-uri*</c> family, e.g.
/// <c>css-values/inline-cache-base-uri-cssom</c>).
/// <para>
/// This transform rewrites relative <c>url()</c> references in every <c>&lt;style&gt;</c>
/// against the document's first <c>&lt;base href&gt;</c> so they reach the renderer already
/// resolved. A root-relative base (<c>/images/</c>) keeps the resolved URL root-relative, so a
/// host-relative resource mapper (the WPT <c>wptRoot</c> handler, which serves <c>/x</c> from
/// the test root) still recognises it; an absolute base resolves to an absolute URL.
/// </para>
/// <para>
/// Runs inside <see cref="ApplySerializationTransforms"/> after
/// <see cref="InlineStyleSheetImports"/> (so inlined <c>@import</c> content — already rebased
/// onto the imported sheet's own URL — carries absolute <c>url()</c>s this pass leaves alone).
/// Only the render-bound serialization is affected; the live CSSOM rule model and JS-visible
/// serialization are untouched. <c>&lt;style&gt;</c> <c>url()</c>s and
/// <c>&lt;link rel="stylesheet"&gt;</c> <c>href</c>s are rebased against <c>&lt;base&gt;</c>;
/// other element <c>src</c>/<c>href</c> attributes still resolve without <c>&lt;base&gt;</c>.
/// </para>
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>
    /// Rewrites relative <c>url()</c> references in every <c>&lt;style&gt;</c> in the tree
    /// against the document's first <c>&lt;base href&gt;</c>, and likewise resolves each
    /// <c>&lt;link rel="stylesheet"&gt;</c>'s relative <c>href</c> so the linked sheet is
    /// fetched from the base-relative location rather than the document's own directory. A
    /// no-op (byte-identical output) when the document declares no usable <c>&lt;base href&gt;</c>.
    /// </summary>
    private void ApplyBaseHrefToStyleUrls(DomElement root)
    {
        if (!TryFindDocumentBaseHref(root, out var baseHref))
            return;

        RewriteStyleElementUrls(root, baseHref);
        RewriteLinkStyleSheetHrefs(root, baseHref);
    }

    /// <summary>
    /// Resolves the relative <c>href</c> of every <c>&lt;link rel="stylesheet"&gt;</c> against
    /// the document's first <c>&lt;base href&gt;</c>. The renderer resolves a linked sheet's
    /// <c>href</c> against the document URL and never consults <c>&lt;base&gt;</c>, so
    /// <c>&lt;base href="resources/"&gt;</c> before <c>&lt;link href="stylesheet.css"&gt;</c>
    /// fetched the sheet from the document's own directory instead of <c>resources/</c> — it
    /// was not found and the unstyled fallback showed through (WPT
    /// <c>html/semantics/document-metadata/the-link-element/stylesheet-with-base</c>). Absolute,
    /// <c>data:</c>, root-relative, and fragment hrefs are left untouched.
    /// </summary>
    private void RewriteLinkStyleSheetHrefs(DomElement root, string baseHref)
    {
        foreach (var element in root.Descendants().OfType<DomElement>())
        {
            if (!element.TagName.Equals("link", StringComparison.OrdinalIgnoreCase) ||
                !LinkRelIsStyleSheet(element) ||
                !TryGetAttribute(element, "href", out var href) ||
                string.IsNullOrWhiteSpace(href))
                continue;

            var resolved = ResolveUrlAgainstBaseHref(href, baseHref);
            if (resolved is not null)
                SetAttr(element, "href", resolved);
        }
    }

    /// <summary>Whether a <c>&lt;link&gt;</c>'s space-separated <c>rel</c> token list includes
    /// <c>stylesheet</c> (case-insensitive), so only sheet links have their href re-based.</summary>
    private static bool LinkRelIsStyleSheet(DomElement element)
    {
        if (!TryGetAttribute(element, "rel", out var rel) || string.IsNullOrWhiteSpace(rel))
            return false;

        foreach (var token in rel.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (token.Equals("stylesheet", StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    /// <summary>The first <c>&lt;base&gt;</c> in document order with a non-empty
    /// <c>href</c> — the element that sets the document base URL (HTML §4.2.3).</summary>
    private bool TryFindDocumentBaseHref(DomElement root, out string baseHref)
    {
        baseHref = string.Empty;
        foreach (var element in root.Descendants().OfType<DomElement>())
        {
            if (element.TagName.Equals("base", StringComparison.OrdinalIgnoreCase) &&
                TryGetAttribute(element, "href", out var href) &&
                !string.IsNullOrWhiteSpace(href))
            {
                baseHref = href.Trim();
                return true;
            }
        }

        return false;
    }

    private void RewriteStyleElementUrls(DomElement element, string baseHref)
    {
        if (!IsText(element) &&
            element.TagName.Equals("style", StringComparison.OrdinalIgnoreCase))
        {
            var original = GetStyleElementCssText(element);
            var rewritten = RewriteCssUrlsAgainstBaseHref(original, baseHref);
            if (!string.Equals(rewritten, original, StringComparison.Ordinal))
                SetElementTextContent(element, rewritten);
        }

        foreach (var child in ChildElements(element))
            RewriteStyleElementUrls(child, baseHref);
    }

    /// <summary>Rewrites each relative <c>url(...)</c> in <paramref name="css"/> to its
    /// resolution against <paramref name="baseHref"/>; absolute, <c>data:</c>, and fragment
    /// references are left byte-identical.</summary>
    private string RewriteCssUrlsAgainstBaseHref(string css, string baseHref)
        => UrlFunctionPattern.Replace(css, match =>
        {
            var resolved = ResolveUrlAgainstBaseHref(match.Groups[2].Value, baseHref);
            return resolved is null ? match.Value : $"url(\"{resolved}\")";
        });

    /// <summary>
    /// Resolves a CSS <c>url()</c> value or a <c>&lt;link&gt;</c> href against a
    /// <c>&lt;base href&gt;</c>. Delegates to <see cref="HtmlBaseHref.Resolve"/>, the shared
    /// seam this and the WPT runner's stylesheet inliner both go through — see that type for
    /// the resolution rules and why there is only one implementation.
    /// </summary>
    private string? ResolveUrlAgainstBaseHref(string rawUrl, string baseHref) =>
        HtmlBaseHref.Resolve(rawUrl, baseHref, _pageUrl);

    /// <summary>
    /// The URL a <c>&lt;link rel="stylesheet"&gt;</c>'s sheet is read from: a <c>data:</c> href
    /// verbatim, anything else resolved against the <em>document base URL</em>. The verbatim case
    /// matters — round tripping a data: URL through <see cref="Uri"/> normalizes the percent-escapes
    /// its payload is made of, and the payload is the stylesheet.
    /// <para>
    /// The base is the document's, not the page's: a <c>&lt;base href&gt;</c> relocates a linked
    /// sheet (HTML §4.2.3), which is what the render-bound
    /// <see cref="RewriteLinkStyleSheetHrefs"/> pass already honours on the serialization
    /// projection. Resolving against the page URL here instead would read a *different* sheet than
    /// the one that paints whenever the document declares a base.
    /// </para>
    /// </summary>
    private string ResolveStyleSheetLinkUrl(string href) =>
        href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? href
            : ResolveAgainstDocumentBaseUrl(href);

    /// <summary>
    /// Resolves a content-attribute URL against the document base URL — the document's first
    /// <c>&lt;base href&gt;</c> resolved against the page URL when it declares one, the page URL
    /// otherwise. Leaves the value untouched when neither is usable as a base.
    /// </summary>
    private string ResolveAgainstDocumentBaseUrl(string url) =>
        Uri.TryCreate(DocumentBaseUrl(), UriKind.Absolute, out var baseUri) &&
        Uri.TryCreate(baseUri, url, out var resolved)
            ? resolved.AbsoluteUri
            : url;

    private ulong _documentBaseUrlVersion;
    private string? _documentBaseUrlCache;

    /// <summary>
    /// The document base URL, cached against <see cref="DomDocument.Version"/>.
    /// </summary>
    /// <remarks>
    /// Finding the <c>&lt;base href&gt;</c> means walking every descendant, and the overwhelming
    /// majority of documents declare none — the same reason
    /// <see cref="InlineStyleSheetImports(DomElement)"/> resolves its base lazily and at most once.
    /// Here the callers are per-<c>&lt;link&gt;</c> rather than per-document, so without a cache a
    /// page with many sheets pays that walk once for each of them. The version counter is bumped by
    /// every tree edit and attribute write, so adding, removing or re-pointing a <c>&lt;base&gt;</c>
    /// invalidates this on the next call.
    /// </remarks>
    private string DocumentBaseUrl()
    {
        var version = _document.Version;
        if (_documentBaseUrlCache is null || _documentBaseUrlVersion != version)
        {
            _documentBaseUrlCache = HtmlBaseHref.ResolveDocumentBaseUrl(
                _pageUrl, TryFindDocumentBaseHref(DocumentElement, out var baseHref) ? baseHref : null);
            _documentBaseUrlVersion = version;
        }

        return _documentBaseUrlCache;
    }
}
