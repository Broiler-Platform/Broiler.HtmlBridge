using System.Text;
using System.Text.RegularExpressions;
using Broiler.Dom;
using Broiler.HtmlBridge.Internal.Scripting;

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
    /// <summary>Depth cap for nested <c>@import</c> chains — a backstop against a
    /// pathological chain; well above any real stylesheet's nesting.</summary>
    private const int MaxImportDepth = 32;

    private static readonly System.Text.RegularExpressions.Regex UrlFunctionPattern = new(
        @"url\(\s*(['""]?)([^'""\)]+)\1\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

    /// <summary>Quick, allocation-free check for a leading <c>@import</c> before the
    /// heavier scan/fetch runs.</summary>
    private static bool HasLeadingImport(string css)
        => ScanLeadingImports(css).Imports.Count > 0;

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

    /// <summary>
    /// Scans the leading portion of a stylesheet — whitespace, comments, and any
    /// <c>@charset</c>/<c>@import</c> statements, which per CSS syntax must precede all
    /// style rules — collecting each <c>@import</c>'s href and media condition and the
    /// offset at which the first non-import content begins. A stray <c>@import</c> after a
    /// style rule is invalid and left in place (the renderer ignores it, matching browsers).
    /// </summary>
    private static (int EndOffset, List<(string Href, string Media)> Imports) ScanLeadingImports(string css)
    {
        var imports = new List<(string, string)>();
        var i = 0;
        var n = css.Length;
        var consumedEnd = 0;

        while (i < n)
        {
            // Skip whitespace.
            while (i < n && char.IsWhiteSpace(css[i]))
                i++;

            // Skip /* comments */.
            if (i + 1 < n && css[i] == '/' && css[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(css[i] == '*' && css[i + 1] == '/'))
                    i++;
                i = System.Math.Min(n, i + 2);
                continue;
            }

            if (StartsWithAtKeyword(css, i, "@charset"))
            {
                i = ConsumeToSemicolon(css, i);
                consumedEnd = i;
                continue;
            }

            if (StartsWithAtKeyword(css, i, "@import"))
            {
                var stmtEnd = ConsumeToSemicolon(css, i);
                var prelude = css[(i + "@import".Length)..stmtEnd].Trim().TrimEnd(';').Trim();
                imports.Add(ParseImportPrelude(prelude));
                i = stmtEnd;
                consumedEnd = i;
                continue;
            }

            break;
        }

        return (consumedEnd, imports);
    }

    /// <summary>Whether <paramref name="css"/> at <paramref name="pos"/> begins the at-rule
    /// <paramref name="keyword"/> (case-insensitive) followed by a non-identifier delimiter,
    /// so <c>@import</c> is not matched inside <c>@imports-like</c>.</summary>
    private static bool StartsWithAtKeyword(string css, int pos, string keyword)
    {
        if (pos + keyword.Length > css.Length)
            return false;
        if (string.Compare(css, pos, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
            return false;
        if (pos + keyword.Length == css.Length)
            return true;
        var next = css[pos + keyword.Length];
        return char.IsWhiteSpace(next) || next == '"' || next == '\'' || next == '(' || next == ';';
    }

    /// <summary>Returns the index just past the terminating <c>;</c> of a statement starting
    /// at <paramref name="start"/>, ignoring semicolons inside strings, <c>url(...)</c>
    /// parentheses, and comments. Falls back to end-of-input for an unterminated statement.</summary>
    private static int ConsumeToSemicolon(string css, int start)
    {
        var i = start;
        var n = css.Length;
        while (i < n)
        {
            var c = css[i];
            if (c == '"' || c == '\'')
            {
                var quote = c;
                i++;
                while (i < n && css[i] != quote)
                {
                    if (css[i] == '\\' && i + 1 < n)
                        i++;
                    i++;
                }
                i++;
                continue;
            }
            if (c == '/' && i + 1 < n && css[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < n && !(css[i] == '*' && css[i + 1] == '/'))
                    i++;
                i = System.Math.Min(n, i + 2);
                continue;
            }
            if (c == '(')
            {
                var depth = 1;
                i++;
                while (i < n && depth > 0)
                {
                    if (css[i] == '(') depth++;
                    else if (css[i] == ')') depth--;
                    i++;
                }
                continue;
            }
            if (c == ';')
                return i + 1;
            i++;
        }
        return n;
    }

    /// <summary>Splits an <c>@import</c> prelude (after the keyword, without the trailing
    /// <c>;</c>) into its href and the remaining media condition.</summary>
    private static (string Href, string Media) ParseImportPrelude(string prelude)
    {
        if (prelude.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            var open = prelude.IndexOf('(');
            var close = prelude.IndexOf(')', open + 1);
            if (open >= 0 && close > open)
            {
                var href = prelude[(open + 1)..close].Trim().Trim('"', '\'');
                return (href, prelude[(close + 1)..].Trim());
            }
        }
        else if (prelude.Length > 0 && (prelude[0] == '"' || prelude[0] == '\''))
        {
            var quote = prelude[0];
            var close = prelude.IndexOf(quote, 1);
            if (close > 0)
                return (prelude[1..close], prelude[(close + 1)..].Trim());
        }

        return (string.Empty, string.Empty);
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

    /// <summary>
    /// Rewrites relative <c>url(...)</c> references in imported CSS to absolute URLs against
    /// the imported sheet's own URL, so an imported sheet's relative images resolve against
    /// the sheet rather than the importing document. Absolute, <c>data:</c>, and fragment
    /// references are left untouched; when the base is itself a <c>data:</c> URL (no path to
    /// resolve against) the text is returned unchanged.
    /// </summary>
    private static string RebaseRelativeUrls(string css, string baseUrl)
    {
        if (baseUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return css;

        return UrlFunctionPattern.Replace(css, match =>
        {
            var raw = match.Groups[2].Value.Trim();
            if (raw.Length == 0 ||
                raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("#", StringComparison.Ordinal) ||
                HasUrlScheme(raw))
                return match.Value;

            var resolved = UrlResolver.Resolve(raw, baseUrl)?.AbsoluteUri;
            return resolved is null ? match.Value : $"url(\"{resolved}\")";
        });
    }

    /// <summary>Whether a URL reference already carries an explicit scheme
    /// (<c>scheme:</c> or protocol-relative <c>//host</c>), so it should not be re-based.</summary>
    private static bool HasUrlScheme(string url)
    {
        if (url.StartsWith("//", StringComparison.Ordinal))
            return true;
        var colon = url.IndexOf(':');
        if (colon <= 0)
            return false;
        for (var i = 0; i < colon; i++)
        {
            var c = url[i];
            if (!char.IsLetterOrDigit(c) && c != '+' && c != '-' && c != '.')
                return false;
        }
        return true;
    }
}
