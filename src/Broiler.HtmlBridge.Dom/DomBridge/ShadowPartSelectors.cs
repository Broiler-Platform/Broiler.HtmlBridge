using System.Linq;
using System.Text;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// Render-time projection of <c>::part()</c> selectors (CSS Shadow Parts 1).
/// <para>
/// A shadow tree is serialized into the render document as ordinary descendants of its host, and
/// the renderer's selector matcher does not model <c>::part</c> — so an outer
/// <c>::part(p2) { background: yellow; width: 100px; height: 100px }</c> reached nothing and the
/// part rendered with no author styling at all. That is why WPT
/// <c>css/css-view-transitions/auto-name-from-id-shadow</c> (issue #1544 problem 11, 0.6% match)
/// showed only its green light-DOM item: the yellow one is a shadow part with no size and no
/// colour of its own.
/// </para>
/// <para>
/// Each <c>::part()</c> rule gets a renderer-readable twin, injected as one extra bridge sheet:
/// </para>
/// <list type="bullet">
///   <item>every element inside a shadow root that carries a <c>part</c> attribute is stamped
///         <c>data-broiler-part="&lt;its part list&gt;"</c>, so only a real shadow part is
///         addressable — a <c>part</c> attribute on a light-DOM element is inert and must stay so;</item>
///   <item><c>&lt;prefix&gt;::part(a b)</c> is re-emitted as
///         <c>&lt;prefix&gt; [data-broiler-part~="a"][data-broiler-part~="b"]</c>, every ident in
///         the argument being required per the spec's <c>&lt;ident&gt;+</c> grammar;</item>
///   <item>a bare <c>::part(a)</c>, which Chromium accepts as <c>*::part(a)</c>, becomes just the
///         attribute compound.</item>
/// </list>
/// <para>
/// <b>Why a twin sheet rather than rewriting the author's rule in place.</b> The author's
/// <c>::part(</c> text is load-bearing elsewhere: <c>AppendOuterPartRules</c> scans for it to lift
/// those rules into a shadow tree's computed-style scope, which is how <c>getComputedStyle</c> — and
/// so a view transition looking for <c>view-transition-name</c> — sees them at all. Rewriting the
/// text away silently broke that path (<c>Outer_Part_Rules_Reach_Into_The_Shadow_Tree</c> caught it).
/// Adding a sheet leaves every author selector exactly as written.
/// </para>
/// <para>
/// <b>Two deliberate approximations.</b> The descendant combinator is looser than the spec's "part
/// of the shadow tree whose host is the originating element": it also reaches parts of a
/// <em>nested</em> shadow tree, which really require <c>exportparts</c> to be addressable. Encoding
/// the host relationship needs selector-engine support that does not exist here. And the twin's
/// specificity is that of its attribute compound, where <c>::part()</c> is specced as a
/// pseudo-element; the twins are injected after the author sheets, so between themselves their
/// author order still decides ties.
/// </para>
/// <para>
/// Only the render-bound serialization is affected; the live CSSOM and JS-visible
/// <c>innerHTML</c> still expose the author's <c>::part</c> text.
/// </para>
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>Marker attribute carrying a shadow part's name list.</summary>
    private const string ShadowPartAttr = "data-broiler-part";

    /// <summary>
    /// Stamps every shadow part in the tree and injects a renderer-readable twin of each
    /// <c>::part()</c> rule. A no-op for a document with no <c>::part</c> rules.
    /// </summary>
    private void RewriteShadowPartSelectors(DomElement root)
    {
        var styles = new List<DomElement>();
        CollectStyleElementsForParts(root, styles);
        if (styles.Count == 0)
            return;

        var rules = new List<string>();
        foreach (var style in styles)
            CollectPartRuleTwins(GetStyleElementCssText(style), rules, ShadowPartAttr, prefixlessOnly: false);

        if (rules.Count == 0)
            return;

        foreach (var shadowRoot in root.Descendants().OfType<DomElement>())
        {
            if (!string.Equals(shadowRoot.TagName, "#shadow-root", StringComparison.Ordinal))
                continue;

            foreach (var element in shadowRoot.Descendants().OfType<DomElement>())
            {
                if (TryGetAttribute(element, "part", out var part) && !string.IsNullOrWhiteSpace(part))
                    SetAttr(element, ShadowPartAttr, part);
            }
        }

        InjectBridgeStyleRules(root, rules);
    }

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
    private static string ExtractPartRulesForShadowScope(string css)
    {
        var rules = new List<string>();
        CollectPartRuleTwins(css, rules, ShadowScopePartAttr, prefixlessOnly: true);
        return rules.Count == 0 ? string.Empty : string.Join("\n", rules);
    }

    /// <summary>The attribute a shadow-scope twin matches: the element's own <c>part</c>.</summary>
    private const string ShadowScopePartAttr = "part";

    /// <summary>Collects every <c>&lt;style&gt;</c> in the document, shadow trees included — a
    /// shadow tree's own styles may address the parts of a tree nested inside it.</summary>
    private void CollectStyleElementsForParts(DomElement element, List<DomElement> styles)
    {
        foreach (var child in ChildElements(element))
        {
            if (IsText(child))
                continue;

            if (child.TagName.Equals("style", StringComparison.OrdinalIgnoreCase))
                styles.Add(child);

            CollectStyleElementsForParts(child, styles);
        }
    }

    /// <summary>
    /// Adds a twin for each top-level rule whose prelude contains <c>::part(</c>. A brace-depth scan
    /// rather than a parse, mirroring <c>ExtractPartRules</c>: rules nested inside an at-rule are
    /// left alone, so a <c>::part()</c> inside <c>@media</c> still does not reach the renderer.
    /// </summary>
    private static void CollectPartRuleTwins(
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
