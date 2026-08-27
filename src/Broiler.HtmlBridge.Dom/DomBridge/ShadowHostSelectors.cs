using System.Globalization;
using System.Linq;
using System.Text;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// Render-time scoping of <c>:host</c> selectors (CSS Scoping 1 §3.1).
/// <para>
/// A shadow root's <c>&lt;style&gt;</c> is serialized inline into the render document, so the
/// renderer sees its rules as ordinary global rules. The renderer's selector matcher lists
/// <c>host</c>/<c>host-context</c> as recognised-but-unmodelled pseudo-classes, which are
/// deliberately lenient and therefore match <em>every</em> element — so a single
/// <c>:host { background: red }</c> in any shadow tree painted the whole document red (the
/// <c>css/css-shadow</c> reftest family, e.g. <c>css-scoping-shadow-host-functional-rule</c>,
/// whose five hosts must all end up green).
/// </para>
/// <para>
/// The renderer cannot fix this by itself: it has no rule provenance, so it cannot tell which
/// shadow tree a rule came from — and because the rules are global, one host's
/// <c>:host(other-host)</c> rule would still match a <em>different</em> host. That knowledge
/// exists only here, where the owning <c>#shadow-root</c> is known, so rewrite each
/// <c>:host</c> selector to target exactly that root's host element:
/// </para>
/// <list type="bullet">
///   <item><c>:host</c> → <c>[data-broiler-shadow-host="N"]</c></item>
///   <item><c>:host(&lt;compound&gt;)</c> → <c>&lt;compound&gt;[data-broiler-shadow-host="N"]</c>,
///         so the host must match the compound <em>and</em> be this root's host</item>
///   <item><c>:host(&lt;complex&gt;)</c> (an argument containing a combinator, e.g.
///         <c>:host(div host-3)</c>) is an invalid selector per the grammar
///         (<c>&lt;compound-selector&gt;</c> only) and must match nothing</item>
///   <item><c>:host-context(...)</c> is not modelled; it is neutralised to a never-matching
///         selector rather than left to match everything</item>
/// </list>
/// <para>
/// Matching the compound itself is delegated to the renderer, which already supports type,
/// class, id and attribute selectors — so <c>:host(host-2.foo#bar[name=baz])</c> needs no
/// selector engine here. Only the render-bound serialization is affected; the live CSSOM and
/// JS-visible <c>innerHTML</c> still expose the author's <c>:host</c> text.
/// </para>
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>Marker attribute identifying a shadow host, keyed per shadow root.</summary>
    private const string ShadowHostAttr = "data-broiler-shadow-host";

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
    /// Rewrites <c>:host</c> selectors in every shadow root's <c>&lt;style&gt;</c> so they
    /// target that root's host element instead of matching every element. A no-op for a
    /// document with no shadow roots, and for a shadow tree whose styles use no <c>:host</c>.
    /// </summary>
    private void ScopeShadowHostSelectors(DomElement root)
    {
        var index = 0;

        foreach (var element in root.Descendants().OfType<DomElement>())
        {
            if (!string.Equals(element.TagName, "#shadow-root", StringComparison.Ordinal))
                continue;

            var host = ParentEl(element);
            if (host == null)
                continue;

            var token = index.ToString(CultureInfo.InvariantCulture);
            index++;

            var styles = new List<DomElement>();
            CollectStylesInShadowRoot(element, styles);

            var stamped = false;
            foreach (var style in styles)
            {
                var original = GetStyleElementCssText(style);
                var rewritten = RewriteHostSelectors(original, token);
                if (string.Equals(rewritten, original, StringComparison.Ordinal))
                    continue;

                if (!stamped)
                {
                    SetAttr(host, ShadowHostAttr, token);
                    stamped = true;
                }

                SetElementTextContent(style, rewritten);
            }
        }
    }

    /// <summary>
    /// Collects the <c>&lt;style&gt;</c> elements belonging to <paramref name="shadowRoot"/>,
    /// without descending into a nested <c>#shadow-root</c> (whose styles are scoped to their
    /// own host by that root's own pass).
    /// </summary>
    private void CollectStylesInShadowRoot(DomElement shadowRoot, List<DomElement> styles)
    {
        foreach (var child in ChildElements(shadowRoot))
        {
            if (IsText(child))
                continue;

            if (string.Equals(child.TagName, "#shadow-root", StringComparison.Ordinal))
                continue;

            if (child.TagName.Equals("style", StringComparison.OrdinalIgnoreCase))
                styles.Add(child);

            CollectStylesInShadowRoot(child, styles);
        }
    }

    /// <summary>
    /// Rewrites every <c>:host</c> / <c>:host(...)</c> / <c>:host-context(...)</c> occurrence in
    /// <paramref name="css"/> for the shadow host identified by <paramref name="token"/>.
    /// Occurrences inside strings and comments are left untouched.
    /// </summary>
    private static string RewriteHostSelectors(string css, string token)
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
