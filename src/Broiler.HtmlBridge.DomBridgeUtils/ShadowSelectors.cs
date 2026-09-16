using System.Text;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    /// <summary>Marker attribute identifying a shadow host, keyed per shadow root.</summary>
    internal const string ShadowHostAttr = "data-broiler-shadow-host";

    /// <summary>
    /// A syntactically valid selector that can never match: the marker attribute is only ever
    /// stamped with a numeric token, so this sentinel value matches no element. Used for
    /// <c>:host</c> forms that must not match (invalid arguments, unmodelled
    /// <c>:host-context()</c>).
    /// </summary>
    private const string NeverMatchesSelector = "[" + ShadowHostAttr + "=\"none\"]";

    private static string HostAttrSelector(string token) =>
        "[" + ShadowHostAttr + "=\"" + token + "\"]";

    /// <summary>
    /// Rewrites every <c>:host</c> / <c>:host(...)</c> / <c>:host-context(...)</c> occurrence in
    /// <paramref name="css"/> for the shadow host identified by <paramref name="token"/>.
    /// Occurrences inside strings and comments are left untouched.
    /// </summary>
    internal static string RewriteHostSelectors(string css, string token)
    {
        if (string.IsNullOrEmpty(css) ||
            css.IndexOf(":host", StringComparison.OrdinalIgnoreCase) < 0)
            return css;

        var sb = new StringBuilder(css.Length + 32);
        var i = 0;
        var n = css.Length;

        while (i < n)
        {
            var c = css[i];

            // Preserve strings verbatim — a `content: ":host"` value is not a selector.
            if (c == '"' || c == '\'')
            {
                var quote = c;
                sb.Append(c);
                i++;
                while (i < n)
                {
                    sb.Append(css[i]);
                    if (css[i] == '\\' && i + 1 < n)
                    {
                        i++;
                        if (i < n) sb.Append(css[i]);
                        i++;
                        continue;
                    }
                    if (css[i] == quote) { i++; break; }
                    i++;
                }
                continue;
            }

            // Preserve comments verbatim.
            if (c == '/' && i + 1 < n && css[i + 1] == '*')
            {
                var end = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? n : end + 2;
                sb.Append(css, i, end - i);
                i = end;
                continue;
            }

            if (c == ':' && MatchesHostKeyword(css, i, out var afterKeyword, out var isHostContext))
            {
                // `:host-context(...)` is not modelled — neutralise it (it must not match
                // everything). Its argument, when present, is consumed with it.
                if (isHostContext)
                {
                    if (afterKeyword < n && css[afterKeyword] == '(')
                        afterKeyword = SkipBalancedParens(css, afterKeyword);
                    sb.Append(NeverMatchesSelector);
                    i = afterKeyword;
                    continue;
                }

                if (afterKeyword < n && css[afterKeyword] == '(')
                {
                    var close = SkipBalancedParens(css, afterKeyword);
                    var args = css[(afterKeyword + 1)..(close - 1)].Trim();

                    // `:host()` takes a <compound-selector>; an empty argument, a selector list,
                    // or anything containing a combinator is invalid and matches nothing.
                    if (args.Length == 0 || HasTopLevelCombinatorOrList(args))
                        sb.Append(NeverMatchesSelector);
                    else
                        sb.Append(args).Append(HostAttrSelector(token));

                    i = close;
                    continue;
                }

                sb.Append(HostAttrSelector(token));
                i = afterKeyword;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Whether <paramref name="css"/> at <paramref name="pos"/> (a <c>:</c>) begins the
    /// <c>:host</c> or <c>:host-context</c> pseudo-class — and not merely an identifier that
    /// starts with those letters (e.g. <c>:hostile</c>).
    /// </summary>
    private static bool MatchesHostKeyword(string css, int pos, out int afterKeyword, out bool isHostContext)
    {
        afterKeyword = pos;
        isHostContext = false;

        const string host = ":host";
        const string hostContext = ":host-context";

        if (string.Compare(css, pos, hostContext, 0, hostContext.Length, StringComparison.OrdinalIgnoreCase) == 0 &&
            !IsIdentChar(CharAt(css, pos + hostContext.Length)))
        {
            afterKeyword = pos + hostContext.Length;
            isHostContext = true;
            return true;
        }

        if (string.Compare(css, pos, host, 0, host.Length, StringComparison.OrdinalIgnoreCase) == 0 &&
            !IsIdentChar(CharAt(css, pos + host.Length)))
        {
            afterKeyword = pos + host.Length;
            return true;
        }

        return false;
    }

    private static char CharAt(string s, int i) => i < s.Length ? s[i] : '\0';

    /// <summary>Whether <paramref name="c"/> can continue a CSS identifier (so <c>:host-context</c>
    /// is not mistaken for <c>:host</c> followed by <c>-context</c>).</summary>
    private static bool IsIdentChar(char c) =>
        char.IsLetterOrDigit(c) || c == '-' || c == '_' || c > 0x7F;

    /// <summary>Returns the index just past the <c>)</c> matching the <c>(</c> at
    /// <paramref name="open"/>, ignoring parentheses inside strings.</summary>
    private static int SkipBalancedParens(string css, int open)
    {
        var depth = 0;
        var i = open;
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
                    if (css[i] == '\\' && i + 1 < n) i++;
                    i++;
                }
                i++;
                continue;
            }

            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0) return i + 1;
            }

            i++;
        }

        return n;
    }

    /// <summary>
    /// Whether a <c>:host()</c> argument is something other than a single compound selector —
    /// i.e. it contains a descendant/child/sibling combinator or a comma — scanning only at the
    /// top level (inside <c>[...]</c>, <c>(...)</c> and strings does not count, so
    /// <c>host-2.foo#bar[name=baz]</c> and <c>:not(.a)</c> stay compound).
    /// </summary>
    private static bool HasTopLevelCombinatorOrList(string args)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var i = 0;
        var n = args.Length;

        while (i < n)
        {
            var c = args[i];

            if (c == '"' || c == '\'')
            {
                var quote = c;
                i++;
                while (i < n && args[i] != quote)
                {
                    if (args[i] == '\\' && i + 1 < n) i++;
                    i++;
                }
                i++;
                continue;
            }

            if (c == '(') parenDepth++;
            else if (c == ')') parenDepth--;
            else if (c == '[') bracketDepth++;
            else if (c == ']') bracketDepth--;
            else if (parenDepth == 0 && bracketDepth == 0 &&
                     (char.IsWhiteSpace(c) || c == '>' || c == '+' || c == '~' || c == ','))
            {
                return true;
            }

            i++;
        }

        return false;
    }
}

public static partial class DomBridgeUtils
{
    /// <summary>Marker attribute carrying a shadow part's name list.</summary>
    internal const string ShadowPartAttr = "data-broiler-part";

    /// <summary>
    /// The <c>::part()</c> rules of an outer stylesheet, re-emitted for a shadow tree's own
    /// computed-style scope: each <c>::part(a b)</c> becomes <c>[part~="a"][part~="b"]</c>, matching
    /// the shadow element's real attribute. No <c>data-broiler-part</c> stamp is needed here — this
    /// scope only ever holds elements of the one shadow tree, so the attribute alone carries the
    /// meaning the pseudo did.
    /// <para>
    /// Only a <em>prefix-less</em> <c>::part()</c> is lifted. A prefixed one (<c>#host::part(p)</c>)
    /// names which host's parts it styles, and that prefix cannot be matched from inside the shadow
    /// scope; dropping it would let one host's rule style another host's identically-named part.
    /// Leaving those unlifted keeps them where they already were rather than making them wrong.
    /// </para>
    /// </summary>
    internal static string ExtractPartRulesForShadowScope(string css)
    {
        var rules = new List<string>();
        CollectPartRuleTwins(css, rules, ShadowScopePartAttr, prefixlessOnly: true);
        return rules.Count == 0 ? string.Empty : string.Join("\n", rules);
    }

    /// <summary>The attribute a shadow-scope twin matches: the element's own <c>part</c>.</summary>
    private const string ShadowScopePartAttr = "part";

    /// <summary>
    /// Adds a twin for each top-level rule whose prelude contains <c>::part(</c>. A brace-depth scan
    /// rather than a parse, mirroring <c>ExtractPartRules</c>: rules nested inside an at-rule are
    /// left alone, so a <c>::part()</c> inside <c>@media</c> still does not reach the renderer.
    /// </summary>
    internal static void CollectPartRuleTwins(
        string css, List<string> rules, string attribute, bool prefixlessOnly)
    {
        if (string.IsNullOrEmpty(css) || css.IndexOf("::part(", StringComparison.OrdinalIgnoreCase) < 0)
            return;

        var depth = 0;
        var ruleStart = 0;
        var preludeEnd = -1;

        for (var index = 0; index < css.Length; index++)
        {
            var character = css[index];
            if (character == '{')
            {
                if (depth == 0)
                    preludeEnd = index;
                depth++;
            }
            else if (character == '}')
            {
                depth--;
                if (depth != 0)
                    continue;

                if (preludeEnd > ruleStart)
                {
                    var prelude = css[ruleStart..preludeEnd];
                    if (prelude.IndexOf("::part(", StringComparison.OrdinalIgnoreCase) >= 0
                        && RewritePartSelectorList(prelude, attribute, prefixlessOnly) is { } rewritten)
                    {
                        var body = css[(preludeEnd + 1)..index];
                        rules.Add($"{rewritten} {{{body}}}");
                    }
                }

                ruleStart = index + 1;
                preludeEnd = -1;
            }
        }
    }

    /// <summary>
    /// Rewrites every <c>::part(...)</c> in a selector prelude to the attribute compound that stands
    /// in for it, or returns <c>null</c> when an occurrence is not a plain ident list — guessing at
    /// one could produce a selector that matches the wrong elements rather than none.
    /// </summary>
    private static string? RewritePartSelectorList(string prelude, string attribute, bool prefixlessOnly)
    {
        var sb = new StringBuilder(prelude.Length + 32);
        var i = 0;
        var n = prelude.Length;

        while (i < n)
        {
            var c = prelude[i];

            // Preserve strings verbatim — an attribute selector's value is not a selector.
            if (c is '"' or '\'')
            {
                var quote = c;
                sb.Append(c);
                i++;
                while (i < n)
                {
                    sb.Append(prelude[i]);
                    if (prelude[i] == '\\' && i + 1 < n)
                    {
                        i++;
                        if (i < n) sb.Append(prelude[i]);
                        i++;
                        continue;
                    }
                    if (prelude[i] == quote) { i++; break; }
                    i++;
                }
                continue;
            }

            if (c == '/' && i + 1 < n && prelude[i + 1] == '*')
            {
                var end = prelude.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? n : end + 2;
                sb.Append(prelude, i, end - i);
                i = end;
                continue;
            }

            if (c == ':' && MatchesPartKeyword(prelude, i, out var afterKeyword)
                && afterKeyword < n && prelude[afterKeyword] == '(')
            {
                var close = SkipBalancedParens(prelude, afterKeyword);
                var compound = PartAttrCompound(prelude[(afterKeyword + 1)..(close - 1)], attribute);
                if (compound is null)
                    return null;

                bool hasPrefix = sb.Length > 0 && !IsSelectorBoundary(sb[^1]);
                if (prefixlessOnly && hasPrefix)
                    return null;

                // `#host::part(p)` needs a descendant combinator inserted; `main ::part(p)` and a
                // `::part(p)` starting a selector already read as one.
                if (hasPrefix)
                    sb.Append(' ');

                sb.Append(compound);
                i = close;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>Whether <paramref name="css"/> has the <c>::part</c> keyword at
    /// <paramref name="index"/>, and where it ends.</summary>
    private static bool MatchesPartKeyword(string css, int index, out int afterKeyword)
    {
        afterKeyword = index;
        const string keyword = "::part";
        if (index + keyword.Length > css.Length)
            return false;
        if (string.Compare(css, index, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) != 0)
            return false;

        afterKeyword = index + keyword.Length;
        return true;
    }

    /// <summary>
    /// Turns a <c>::part()</c> argument — one or more idents, all of which must be present — into
    /// the attribute compound that stands in for it, or <c>null</c> when it is not a plain ident list.
    /// </summary>
    private static string? PartAttrCompound(string argument, string attribute)
    {
        var idents = argument.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (idents.Length == 0)
            return null;

        var sb = new StringBuilder();
        foreach (var ident in idents)
        {
            foreach (var ch in ident)
            {
                if (!char.IsLetterOrDigit(ch) && ch != '-' && ch != '_')
                    return null;
            }

            sb.Append('[').Append(attribute).Append("~=\"").Append(ident).Append("\"]");
        }

        return sb.ToString();
    }

    /// <summary>Whether the character already separates two compounds, so no descendant combinator
    /// needs inserting before the rewritten part compound.</summary>
    private static bool IsSelectorBoundary(char c) =>
        char.IsWhiteSpace(c) || c is ',' or '>' or '+' or '~' or '(';
}
