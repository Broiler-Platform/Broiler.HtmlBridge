using System;
using Broiler.Dom.Html;
using Broiler.HtmlBridge.Dom;

namespace Broiler.HtmlBridge;

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
    /// <summary>
    /// Finds the document base URL in raw HTML source: the <c>href</c> of the
    /// first <c>&lt;base&gt;</c> in document order that carries a non-whitespace
    /// one, trimmed. For callers that hold a parsed tree, walking the DOM is
    /// equivalent — this overload exists for the pre-DOM stages (the WPT runner
    /// reads the test file as text).
    /// <para>
    /// Skipping a blank <c>href</c> is not HTML §4.2.3, which takes the first
    /// <c>&lt;base&gt;</c> with an <c>href</c> attribute of any value; it is the rule
    /// the DOM walk this must agree with applies (<c>DomBridge.TryFindDocumentBaseHref</c>).
    /// </para>
    /// <para>
    /// The source is read as the start tags the shared <see cref="HtmlTokenizer"/>
    /// produces, the same tokens the DOM is built from, so the answer is the one
    /// walking that DOM gives, with the one exception below. A
    /// <c>&lt;base&gt;</c> spelled inside a comment, in <c>script</c>, <c>style</c>
    /// or <c>noscript</c> text, in another tag's attribute value or in a
    /// <c>&lt;template&gt;</c>'s contents is not an element of the document and
    /// does not count; the <c>href</c> is matched by its whole name and its value
    /// has its character references decoded. A self-closing <c>&lt;template/&gt;</c>
    /// opens no contents, as the shared parser treats it.
    /// </para>
    /// <para>
    /// Where the tokenizer departs from the HTML Standard, so does this, and so does
    /// the DOM: the tokenizer reads only <c>script</c>, <c>style</c> and
    /// <c>noscript</c> as raw text, so a <c>&lt;base&gt;</c> inside the text of
    /// <c>title</c> or <c>textarea</c> (RCDATA), of <c>iframe</c>, <c>xmp</c>,
    /// <c>noembed</c> or <c>noframes</c> (raw text), or after <c>plaintext</c>
    /// still counts.
    /// </para>
    /// <para>
    /// The exception is whitespace before the <c>=</c> of <c>href</c>
    /// (<c>&lt;base href = "x/"&gt;</c>). The tokenizer leaves the attribute empty
    /// there, and so does the DOM built from it; this closes the whitespace up first
    /// (<see cref="HtmlSourceAttributes.CloseSpaceBeforeEquals"/>) and reads
    /// <c>x/</c>, as the Standard does and the regular expression this replaced did.
    /// For such a base the WPT runner's inliner and the DOM transform's walk
    /// disagree until the tokenizer is fixed.
    /// </para>
    /// </summary>
    public static bool TryFindBaseHref(string html, out string baseHref)
    {
        baseHref = string.Empty;

        // Base-less source skips tokenizing altogether. The tokenizer lower-cases tag names
        // invariantly, and no character outside ASCII lower-cases to one of these letters, so a
        // source without this text has no base start tag to find.
        if (string.IsNullOrEmpty(html) || !html.Contains("<base", StringComparison.OrdinalIgnoreCase))
            return false;

        html = HtmlSourceAttributes.CloseSpaceBeforeEquals(html, "href");

        // A depth counter rather than a flag: templates nest, and an inner template's end tag must
        // not re-open the outer one's contents to the search.
        var templateDepth = 0;
        foreach (var token in new HtmlTokenizer().Tokenize(html))
        {
            if (token.Type == TokenType.EndTag)
            {
                if (templateDepth > 0 && token.Name == "template")
                    templateDepth--;
                continue;
            }

            if (token.Type != TokenType.StartTag)
                continue;

            if (token.Name == "template")
            {
                if (!token.SelfClosing)
                    templateDepth++;
                continue;
            }

            if (templateDepth == 0 &&
                token.Name == "base" &&
                token.Attributes.TryGetValue("href", out var href) &&
                !string.IsNullOrWhiteSpace(href))
            {
                baseHref = href.Trim();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Resolves <paramref name="rawUrl"/> against <paramref name="baseHref"/>, or
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
