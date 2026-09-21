using System.Text;
using Broiler.CSS;
using Broiler.JSeal;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    internal static string AsciiToLower(string input)
    {
        var characters = input.ToCharArray();
        for (var index = 0; index < characters.Length; index++)
        {
            if (characters[index] is >= 'A' and <= 'Z')
                characters[index] = (char)(characters[index] + 32);
        }
        return new string(characters);
    }
}

public static partial class DomBridgeUtils
{
    internal static JsValue? NamedItem(IJsRealm realm, Func<List<JsValue>> contents, string name)
    {
        if (name.Length == 0)
            return null;

        foreach (var candidate in contents())
        {
            if (candidate.IsObject &&
                (Matches(realm, candidate, "id", name) || Matches(realm, candidate, "name", name)))
                return candidate;
        }

        return null;

        // The attribute has to be a JavaScript *string* to match, exactly as before: an element whose
        // reflected `id` is anything else does not answer the named getter. So this reads the property
        // and tests its kind rather than coercing — realm.ToJsString would run a page toString and
        // make an object match a name it never had.
        static bool Matches(IJsRealm realm, JsValue wrapper, string attribute, string name) =>
            realm.GetProperty(wrapper, attribute) is { IsString: true } value &&
            string.Equals(value.AsString, name, StringComparison.Ordinal);
    }

    /// <summary>
    /// A selector result normalised to what the DOM says it can be: an object or JavaScript
    /// <c>null</c> and never anything else — a wrapper, a <c>NodeList</c>, an <c>HTMLCollection</c>,
    /// or the <c>null</c> a <c>querySelector</c> that matched nothing answers.
    /// </summary>
    /// <remarks>
    /// A filter, not a conversion: the arms it guards all answer an object or <c>null</c> already,
    /// and this is where that invariant is stated.
    /// <c>DomBridge/JsObjects.NonElementNodes.cs</c> is the other caller.
    /// </remarks>
    internal static JsValue FromEngineResult(JsValue value) => value.IsObject ? value : JsValue.Null;
}

public static partial class DomBridgeUtils
{
    /// <summary>Marker attribute identifying membership of a shadow tree, keyed per shadow root.</summary>
    internal const string ShadowScopeAttr = "data-broiler-shadow-scope";

    private static string ScopeAttrSelector(string token) =>
        "[" + ShadowScopeAttr + "=\"" + token + "\"]";

    /// <summary>
    /// At-rules whose block contains style rules rather than declarations, so the rewrite has to
    /// descend into them. Everything else (<c>@keyframes</c>, <c>@font-face</c>, <c>@page</c>,
    /// <c>@property</c>, <c>@counter-style</c>, …) is copied verbatim.
    /// </summary>
    private static readonly HashSet<string> ConditionalGroupAtRules =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "media", "supports", "container", "layer", "scope", "starting-style",
        };

    /// <summary>
    /// Rewrites the selectors of a shadow root's stylesheet so each applies only to elements
    /// carrying <paramref name="token"/>'s scope marker.
    /// </summary>
    internal static string ScopeSelectorsToShadowTree(string css, string token)
    {
        if (string.IsNullOrWhiteSpace(css))
            return css;

        var sb = new StringBuilder(css.Length + 64);
        ScopeRuleList(css, 0, css.Length, token, sb);
        return sb.ToString();
    }

    /// <summary>
    /// Walks a rule list — the top level of a sheet, or the body of a conditional group rule —
    /// emitting each rule with its selector scoped. Strings, comments and bracketed/parenthesized
    /// runs are skipped over so a <c>;</c> or <c>{</c> inside them cannot split a rule.
    /// </summary>
    private static void ScopeRuleList(string css, int start, int end, string token, StringBuilder sb)
    {
        var i = start;
        var preludeStart = start;
        var parens = 0;
        var brackets = 0;

        while (i < end)
        {
            var c = css[i];

            if (c is '"' or '\'') { i = SkipCssString(css, i, end); continue; }
            if (c == '/' && i + 1 < end && css[i + 1] == '*') { i = SkipCssComment(css, i, end); continue; }
            if (c == '(') { parens++; i++; continue; }
            if (c == ')') { if (parens > 0) parens--; i++; continue; }
            if (c == '[') { brackets++; i++; continue; }
            if (c == ']') { if (brackets > 0) brackets--; i++; continue; }

            if (parens > 0 || brackets > 0) { i++; continue; }

            if (c == '{')
            {
                var blockEnd = FindCssBlockEnd(css, i, end);
                EmitScopedRule(css, preludeStart, i, i + 1, blockEnd, end, token, sb);
                i = blockEnd < end ? blockEnd + 1 : end;
                preludeStart = i;
                continue;
            }

            // A statement at-rule (`@import …;`, `@namespace …;`) or a stray `}` carries no
            // selector — copy it through untouched.
            if (c is ';' or '}')
            {
                sb.Append(css, preludeStart, i - preludeStart + 1);
                i++;
                preludeStart = i;
                continue;
            }

            i++;
        }

        if (preludeStart < end)
            sb.Append(css, preludeStart, end - preludeStart);
    }

    private static void EmitScopedRule(
        string css, int preludeStart, int preludeEnd, int blockStart, int blockEnd, int end,
        string token, StringBuilder sb)
    {
        var prelude = css[preludeStart..preludeEnd];
        var trimmed = prelude.TrimStart();

        if (trimmed.StartsWith('@'))
        {
            sb.Append(prelude).Append('{');
            if (ConditionalGroupAtRules.Contains(AtRuleName(trimmed)))
                ScopeRuleList(css, blockStart, blockEnd, token, sb);
            else
                sb.Append(css, blockStart, blockEnd - blockStart);
        }
        else
        {
            sb.Append(ScopeSelectorList(prelude, token)).Append('{');
            // Declaration blocks are copied verbatim: a nested style rule (CSS Nesting) inside
            // one is not rewritten, which is a known limit rather than an oversight.
            sb.Append(css, blockStart, blockEnd - blockStart);
        }

        if (blockEnd < end)
            sb.Append('}');
    }

    /// <summary>The at-rule's name, given text that starts at its <c>@</c>.</summary>
    private static string AtRuleName(string trimmedPrelude)
    {
        var i = 1;
        while (i < trimmedPrelude.Length && IsIdentChar(trimmedPrelude[i]))
            i++;
        return trimmedPrelude[1..i];
    }

    /// <summary>Scopes each complex selector of a comma-separated selector list.</summary>
    private static string ScopeSelectorList(string prelude, string token) =>
        string.Join(", ", CssSyntax.SplitTopLevel(prelude, ',').Select(part => ScopeComplexSelector(part, token)));

    /// <summary>
    /// Appends the scope marker to the subject (last) compound of one complex selector, leaving
    /// the combinators and every other compound exactly as written.
    /// </summary>
    /// <remarks>
    /// The marker goes just after the compound's type selector so that it precedes any
    /// pseudo-element — the renderer's matcher strips everything from <c>::</c> onwards, so an
    /// appended marker would be discarded. <see cref="CssCompoundSelector.TypeSelectorEnd"/> is
    /// that index, measured in <see cref="CssSelector.Text"/>, which is what the parser was handed
    /// with its outer whitespace trimmed; everything else here is copied through untouched.
    /// A selector the parser will not model — an empty list entry, or text that is not a selector
    /// at all — is left exactly as written rather than narrowed on a guess.
    /// </remarks>
    private static string ScopeComplexSelector(string selector, string token)
    {
        if (CssSelectorParser.Parse(selector).Selectors is not [{ Subject: { } subject } parsed])
            return selector;

        // `:host`/`:host-context` address the host, which is not in this tree; `::slotted`/
        // `::part` address light-DOM nodes. Neither may be narrowed to tree membership.
        var compound = parsed.Text.Substring(subject.Start, subject.Length);
        if (ContainsKeyword(compound, ":host") ||
            ContainsKeyword(compound, "::slotted") ||
            ContainsKeyword(compound, "::part"))
            return selector;

        var leading = selector.Length - selector.TrimStart().Length;
        return selector.Insert(leading + subject.TypeSelectorEnd, ScopeAttrSelector(token));
    }

    /// <summary>
    /// Whether <paramref name="compound"/> contains <paramref name="keyword"/> as a whole
    /// pseudo keyword rather than as the prefix of a longer name (<c>:hostile</c>).
    /// </summary>
    private static bool ContainsKeyword(string compound, string keyword)
    {
        var from = 0;
        while (true)
        {
            var at = compound.IndexOf(keyword, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
                return false;

            var after = at + keyword.Length;
            // `:host` must not swallow `:host-context`, which is a distinct keyword this method
            // is also asked about — both are excluded, so a `-` continuation still counts.
            if (after >= compound.Length || !IsIdentChar(compound[after]) || compound[after] == '-')
                return true;

            from = at + 1;
        }
    }

    /// <summary>Index just past the closing quote of the string starting at <paramref name="i"/>.</summary>
    private static int SkipCssString(string css, int i, int end)
    {
        var quote = css[i];
        i++;
        while (i < end)
        {
            if (css[i] == '\\' && i + 1 < end) { i += 2; continue; }
            if (css[i] == quote) return i + 1;
            i++;
        }
        return end;
    }

    /// <summary>Index just past the <c>*&#47;</c> closing the comment starting at <paramref name="i"/>.</summary>
    private static int SkipCssComment(string css, int i, int end)
    {
        var close = css.IndexOf("*/", i + 2, StringComparison.Ordinal);
        return close < 0 || close + 2 > end ? end : close + 2;
    }

    /// <summary>
    /// Index of the <c>}</c> matching the <c>{</c> at <paramref name="open"/>, or
    /// <paramref name="end"/> when the block is unterminated.
    /// </summary>
    private static int FindCssBlockEnd(string css, int open, int end)
    {
        var depth = 0;
        var i = open;

        while (i < end)
        {
            var c = css[i];
            if (c is '"' or '\'') { i = SkipCssString(css, i, end); continue; }
            if (c == '/' && i + 1 < end && css[i + 1] == '*') { i = SkipCssComment(css, i, end); continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0) return i;
            }
            i++;
        }

        return end;
    }
}
