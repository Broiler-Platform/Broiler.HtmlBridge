using System;
using System.Text.RegularExpressions;

namespace Broiler.HtmlBridge;

// From a `Broiler.*` namespace the `Broiler.Regex` NAMESPACE shadows the type, so the
// alias must sit inside the namespace scope to win. This file uses only the .NET Regex.
using Regex = System.Text.RegularExpressions.Regex;
using Match = System.Text.RegularExpressions.Match;

/// <summary>
/// The shared <c>&lt;base href&gt;</c> seam: finding a document's base URL and
/// resolving a relative URL against it.
/// <para>
/// HTML §4.2.3 makes the first <c>&lt;base href&gt;</c> in document order the
/// document base URL, against which every relative URL in the document resolves.
/// Broiler reaches that rule from more than one place — the render-bound
/// serialization transform in <c>DomBridge</c> (<c>&lt;style&gt;</c> <c>url()</c>
/// references and <c>&lt;link rel=stylesheet&gt;</c> hrefs) and the WPT runner's
/// stylesheet inliner, which reads the file off disk before any DOM exists — and
/// each site that reimplemented it got the rule wrong in its own way. This class
/// is the single implementation both call.
/// </para>
/// <para>
/// DIAGNOSTIC NOTE (WPT issue #1491, problem 25):
/// <c>the-link-element/stylesheet-with-base.html</c> sets
/// <c>&lt;base href="resources/"&gt;</c> and links <c>stylesheet.css</c>, with a
/// red sibling sheet next to the test as the trap. <c>e4cc5e9</c> taught the
/// DomBridge transform to honour the base, but the WPT runner inlines linked
/// sheets from disk <em>before</em> that transform runs — resolving against the
/// test's own directory, so it inlined the trap and the test rendered 100% red.
/// If a third site needs base resolution, route it here rather than adding a
/// fourth implementation.
/// </para>
/// </summary>
public static class HtmlBaseHref
{
    private static readonly Regex BaseTagPattern = new(
        @"<base\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HrefAttributePattern = new(
        @"\bhref\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s""'>]+))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Finds the document base URL in raw HTML source: the <c>href</c> of the
    /// first <c>&lt;base&gt;</c> in document order that carries a non-empty one.
    /// For callers that hold a parsed tree, walking the DOM is equivalent — this
    /// overload exists for the pre-DOM stages (the WPT runner reads the test file
    /// as text).
    /// </summary>
    public static bool TryFindBaseHref(string html, out string baseHref)
    {
        baseHref = string.Empty;
        if (string.IsNullOrEmpty(html))
            return false;

        foreach (Match tag in BaseTagPattern.Matches(html))
        {
            var href = HrefAttributePattern.Match(tag.Value);
            if (href.Success && !string.IsNullOrWhiteSpace(href.Groups["v"].Value))
            {
                baseHref = href.Groups["v"].Value.Trim();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves <paramref name="url"/> against <paramref name="baseHref"/>, or
    /// returns <see langword="null"/> when it must be left untouched — empty,
    /// <c>data:</c>, a bare fragment, or already absolute. The result keeps the
    /// base's shape so downstream resource mapping still recognises it:
    /// <list type="bullet">
    ///   <item>an absolute base yields an absolute URL;</item>
    ///   <item>a root-relative base (<c>/images/</c>) yields a root-relative path,
    ///     so a host-relative mapper (the WPT <c>wptRoot</c> handler, which serves
    ///     <c>/x</c> from the test root) still matches;</item>
    ///   <item>a document-relative base (<c>resources/</c>) resolves through
    ///     <paramref name="pageUrl"/> when one is known, and otherwise yields a
    ///     document-relative path — which is what a caller holding a directory
    ///     rather than a URL (the WPT runner) needs.</item>
    /// </list>
    /// <para>
    /// A document-relative base that walks above the document's own directory is
    /// clamped by the path math, matching how the root-relative case already
    /// behaves.
    /// </para>
    /// </summary>
    public static string? Resolve(string rawUrl, string baseHref, string? pageUrl)
    {
        if (rawUrl is null || baseHref is null)
            return null;

        var url = rawUrl.Trim();
        if (url.Length == 0 ||
            url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("#", StringComparison.Ordinal) ||
            HasScheme(url))
            return null;

        if (baseHref.StartsWith("/", StringComparison.Ordinal))
            return ResolveUnderPlaceholderAuthority(url, baseHref, keepLeadingSlash: true);

        // HasScheme guards the same Unix trap as ResolveDocumentBaseUrl: a bare path
        // must not be mistaken for an absolute file: URI.
        if (HasScheme(baseHref) &&
            Uri.TryCreate(baseHref, UriKind.Absolute, out var absoluteBase) &&
            Uri.TryCreate(absoluteBase, url, out var absoluteResolved))
            return Different(absoluteResolved.AbsoluteUri, url);

        if (!string.IsNullOrEmpty(pageUrl) &&
            Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageBase) &&
            Uri.TryCreate(pageBase, baseHref, out var docBase) &&
            Uri.TryCreate(docBase, url, out var docResolved))
            return Different(docResolved.AbsoluteUri, url);

        // A document-relative base with no page URL: do the same path math under a
        // placeholder authority and hand back a document-relative result, so a
        // caller that resolves against a directory rather than a URL can join it.
        return ResolveUnderPlaceholderAuthority(url, baseHref, keepLeadingSlash: false);
    }

    /// <summary>
    /// The document base URL in effect: <paramref name="baseHref"/> resolved
    /// against <paramref name="pageUrl"/> when it is relative, itself when it is
    /// absolute, and <paramref name="pageUrl"/> when there is no usable base.
    /// <para>
    /// This is the URL a relative <c>@import</c> resolves against. HTML §4.2.3
    /// makes <c>&lt;base href&gt;</c> replace the document URL for that purpose,
    /// so an importer that resolves against the page URL alone reaches the wrong
    /// sheet in exactly the way a linked stylesheet did.
    /// </para>
    /// </summary>
    public static string ResolveDocumentBaseUrl(string? pageUrl, string? baseHref)
    {
        var page = pageUrl ?? string.Empty;
        if (string.IsNullOrWhiteSpace(baseHref))
            return page;

        var trimmed = baseHref.Trim();

        // HasScheme first: on a Unix host Uri.TryCreate(UriKind.Absolute) accepts a
        // root-relative path like "/css/" as the FILE path file:///css/, which would
        // silently drop the page's origin.
        if (HasScheme(trimmed) && Uri.TryCreate(trimmed, UriKind.Absolute, out var absolute))
            return absolute.AbsoluteUri;

        if (!string.IsNullOrEmpty(page) &&
            Uri.TryCreate(page, UriKind.Absolute, out var pageBase) &&
            Uri.TryCreate(pageBase, trimmed, out var resolved))
            return resolved.AbsoluteUri;

        return page;
    }

    /// <summary>
    /// Whether <paramref name="url"/> already carries a URL scheme
    /// (<c>https:</c>, <c>file:</c>, the protocol-relative <c>//host/…</c>), in
    /// which case no base applies.
    /// </summary>
    private static bool HasScheme(string url)
    {
        if (url.StartsWith("//", StringComparison.Ordinal))
            return true;

        var colon = url.IndexOf(':');
        if (colon <= 0)
            return false;

        for (int i = 0; i < colon; i++)
        {
            var c = url[i];
            if (!char.IsLetterOrDigit(c) && c != '+' && c != '-' && c != '.')
                return false;
        }

        return char.IsLetter(url[0]);
    }

    private static string? ResolveUnderPlaceholderAuthority(string url, string baseHref, bool keepLeadingSlash)
    {
        var placeholder = new Uri("http://base.invalid", UriKind.Absolute);
        if (!Uri.TryCreate(placeholder, baseHref, out var resolvedBase) ||
            !Uri.TryCreate(resolvedBase, url, out var resolved))
            return null;

        var result = resolved.PathAndQuery + resolved.Fragment;
        if (!keepLeadingSlash)
            result = result.TrimStart('/');

        return Different(result, url);
    }

    /// <summary>The resolved URL, or <see langword="null"/> when resolution was a
    /// no-op — so callers can skip rewriting and keep output byte-identical.</summary>
    private static string? Different(string resolved, string original) =>
        string.Equals(resolved, original, StringComparison.Ordinal) ? null : resolved;
}
