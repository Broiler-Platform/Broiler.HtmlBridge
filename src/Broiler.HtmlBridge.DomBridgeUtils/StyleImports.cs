using System.Text.RegularExpressions;
using Broiler.HtmlBridge.Internal.Scripting;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    /// <summary>Depth cap for nested <c>@import</c> chains — a backstop against a
    /// pathological chain; well above any real stylesheet's nesting.</summary>
    internal const int MaxImportDepth = 32;

    internal static readonly System.Text.RegularExpressions.Regex UrlFunctionPattern = new(
        @"url\(\s*(['""]?)([^'""\)]+)\1\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Quick, allocation-free check for a leading <c>@import</c> before the
    /// heavier scan/fetch runs.</summary>
    internal static bool HasLeadingImport(string css)
        => ScanLeadingImports(css).Imports.Count > 0;

    /// <summary>
    /// Scans the leading portion of a stylesheet — whitespace, comments, and any
    /// <c>@charset</c>/<c>@import</c> statements, which per CSS syntax must precede all
    /// style rules — collecting each <c>@import</c>'s href and media condition and the
    /// offset at which the first non-import content begins. A stray <c>@import</c> after a
    /// style rule is invalid and left in place (the renderer ignores it, matching browsers).
    /// </summary>
    internal static (int EndOffset, List<(string Href, string Media)> Imports) ScanLeadingImports(string css)
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

    /// <summary>
    /// Rewrites relative <c>url(...)</c> references in imported CSS to absolute URLs against
    /// the imported sheet's own URL, so an imported sheet's relative images resolve against
    /// the sheet rather than the importing document. Absolute, <c>data:</c>, and fragment
    /// references are left untouched; when the base is itself a <c>data:</c> URL (no path to
    /// resolve against) the text is returned unchanged.
    /// </summary>
    internal static string RebaseRelativeUrls(string css, string baseUrl)
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
