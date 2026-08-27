using System.Globalization;
using System.Linq;
using System.Text;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// Render-time scoping of a shadow tree's <em>own</em> style rules (CSS Scoping 1 §3.3,
/// "selectors in a shadow tree only match elements in that tree").
/// <para>
/// A shadow root's <c>&lt;style&gt;</c> is serialized inline into the render document, so the
/// renderer sees its rules as ordinary global rules with no provenance —
/// <c>&lt;style&gt;div { background: red }&lt;/style&gt;</c> inside a shadow root repainted
/// <em>every</em> <c>div</c> in the page. That is the whole failure of
/// <c>css/css-shadow/css-scoping-shadow-with-rules-no-style-leak</c> and
/// <c>shadow-link-rel-stylesheet-no-style-leak</c> (both render the light-DOM "FAIL" box red
/// where the reference is green), and — combined with a <c>:dir()</c> that matched
/// everything — it is what painted the whole canvas for
/// <c>shadow-directionality-001/002</c>.
/// </para>
/// <para>
/// <see cref="ScopeShadowHostSelectors"/> already solved the mirror-image problem for
/// <c>:host</c>, and this reuses its shape: stamp the elements, rewrite the selectors.
/// </para>
/// <list type="bullet">
///   <item>every element <em>in</em> the shadow tree gets
///         <c>data-broiler-shadow-scope="N"</c> (the host does not — it belongs to the outer
///         tree and is reachable only through <c>:host</c>);</item>
///   <item>each selector in that root's styles gets <c>[data-broiler-shadow-scope="N"]</c>
///         appended to its <em>subject</em> compound, so the rule can only ever apply to an
///         element of this tree.</item>
/// </list>
/// <para>
/// <b>Why the subject compound only, and not every compound.</b> Scoping the subject is what
/// stops the leak: a rule whose subject is outside the tree cannot apply. Scoping *every*
/// compound would additionally require each ancestor in a descendant selector to be in the
/// same tree — more faithful to the spec, but it adds one attribute selector per compound and
/// so changes specificity <em>unevenly</em> between rules of the same sheet: <c>div span</c>
/// (0,0,2) and <c>.foo</c> (0,1,0) invert their cascade order once they become
/// <c>div[s] span[s]</c> (0,2,2) and <c>.foo[s]</c> (0,2,0). Scoping only the subject adds
/// exactly (0,1,0) to every rule in the sheet, so their order relative to each other is
/// preserved exactly, and the sheet as a whole outranks page rules that reach into the tree —
/// which is the correct direction, since per spec those page rules should not match at all.
/// </para>
/// <para>
/// Left deliberately untouched: a compound containing <c>:host</c>/<c>:host-context</c> (the
/// host is not in the tree; <see cref="ScopeShadowHostSelectors"/> owns it, and runs after
/// this so it still sees the author's keyword), and one containing <c>::slotted</c>/<c>::part</c>
/// (whose subject is a light-DOM node, not a member of this tree). <c>@keyframes</c>,
/// <c>@font-face</c> and friends are copied verbatim — their blocks hold keyframe selectors and
/// descriptors, not selectors — while the conditional group rules (<c>@media</c>,
/// <c>@supports</c>, <c>@container</c>, <c>@layer</c>, <c>@scope</c>, <c>@starting-style</c>)
/// are recursed into, because those do hold style rules.
/// </para>
/// <para>
/// Only the render-bound serialization is affected; the live CSSOM and JS-visible
/// <c>innerHTML</c> still expose the author's original selector text.
/// </para>
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>Marker attribute identifying membership of a shadow tree, keyed per shadow root.</summary>
    private const string ShadowScopeAttr = "data-broiler-shadow-scope";

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
    /// Restricts every shadow root's own style rules to that root's tree. A no-op for a document
    /// with no shadow roots, and for a shadow tree that carries no <c>&lt;style&gt;</c>.
    /// </summary>
    /// <summary>
    /// Whether a shadow root has ever been attached in this document. Both this pass and its
    /// <c>:host</c> sibling look for <c>#shadow-root</c> by walking every descendant, so a document
    /// that has no shadow DOM at all should not pay for the walk on every serialization —
    /// <c>RunTestWithTimeout_GridTemplateColumnsCrash…</c>, a 6-second budget on a pathological
    /// document, is what caught the unguarded version.
    /// </summary>
    private bool _hasShadowRoots;

    private void ScopeShadowTreeSelectors(DomElement root)
    {
        if (!_hasShadowRoots)
            return;

        var index = 0;

        foreach (var element in root.Descendants().OfType<DomElement>())
        {
            if (!string.Equals(element.TagName, "#shadow-root", StringComparison.Ordinal))
                continue;

            var token = index.ToString(CultureInfo.InvariantCulture);
            index++;

            var styles = new List<DomElement>();
            CollectStylesInShadowRoot(element, styles);
            if (styles.Count == 0)
                continue;

            var scoped = false;
            foreach (var style in styles)
            {
                var original = GetStyleElementCssText(style);
                var rewritten = ScopeSelectorsToShadowTree(original, token);
                if (string.Equals(rewritten, original, StringComparison.Ordinal))
                    continue;

                SetElementTextContent(style, rewritten);
                scoped = true;
            }

            if (scoped)
                StampShadowTreeScope(element, token);
        }
    }

    /// <summary>
    /// Stamps every element of <paramref name="shadowRoot"/>'s tree with the scope marker, not
    /// descending into a nested <c>#shadow-root</c> — an inner tree is its own scope, and an
    /// outer tree's rules must not reach into it either.
    /// </summary>
    private void StampShadowTreeScope(DomElement shadowRoot, string token)
    {
        foreach (var child in ChildElements(shadowRoot))
        {
            if (IsText(child) || child.TagName.StartsWith('#'))
                continue;

            SetAttr(child, ShadowScopeAttr, token);
            StampShadowTreeScope(child, token);
        }
    }

    /// <summary>
    /// Rewrites the selectors of a shadow root's stylesheet so each applies only to elements
    /// carrying <paramref name="token"/>'s scope marker.
    /// </summary>
    private static string ScopeSelectorsToShadowTree(string css, string token)
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
    private static string ScopeSelectorList(string prelude, string token)
    {
        var leading = prelude.Length - prelude.TrimStart().Length;
        var body = prelude[leading..];
        if (body.Length == 0)
            return prelude;

        var sb = new StringBuilder(prelude.Length + 32);
        sb.Append(prelude, 0, leading);

        var start = 0;
        var parens = 0;
        var brackets = 0;
        var first = true;

        for (var i = 0; i <= body.Length; i++)
        {
            if (i == body.Length || (body[i] == ',' && parens == 0 && brackets == 0))
            {
                if (!first) sb.Append(", ");
                sb.Append(ScopeComplexSelector(body[start..i], token));
                first = false;
                start = i + 1;
                continue;
            }

            var c = body[i];
            if (c is '"' or '\'') { i = SkipCssString(body, i, body.Length) - 1; continue; }
            if (c == '(') parens++;
            else if (c == ')') { if (parens > 0) parens--; }
            else if (c == '[') brackets++;
            else if (c == ']') { if (brackets > 0) brackets--; }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Appends the scope marker to the subject (last) compound of one complex selector, leaving
    /// the combinators and every other compound exactly as written.
    /// </summary>
    private static string ScopeComplexSelector(string selector, string token)
    {
        var trailing = selector.Length - selector.TrimEnd().Length;
        var body = selector[..(selector.Length - trailing)];
        if (body.Trim().Length == 0)
            return selector;

        var subjectStart = SubjectCompoundStart(body);
        var subject = ScopeCompound(body[subjectStart..], token);
        return body[..subjectStart] + subject + selector[(selector.Length - trailing)..];
    }

    /// <summary>
    /// The index at which the subject compound of <paramref name="selector"/> begins — i.e. just
    /// past the last top-level combinator. Combinator characters inside <c>[...]</c>,
    /// <c>(...)</c> or a string do not count (<c>[a~="b"]</c>, <c>:nth-child(2n+1)</c>).
    /// </summary>
    private static int SubjectCompoundStart(string selector)
    {
        var parens = 0;
        var brackets = 0;
        var start = 0;

        for (var i = 0; i < selector.Length; i++)
        {
            var c = selector[i];
            if (c is '"' or '\'') { i = SkipCssString(selector, i, selector.Length) - 1; continue; }
            if (c == '(') { parens++; continue; }
            if (c == ')') { if (parens > 0) parens--; continue; }
            if (c == '[') { brackets++; continue; }
            if (c == ']') { if (brackets > 0) brackets--; continue; }

            if (parens == 0 && brackets == 0 &&
                (char.IsWhiteSpace(c) || c is '>' or '+' or '~' or '|'))
            {
                // `||` is the column combinator; a lone `|` is a namespace separator and part of
                // the compound, so only treat it as a combinator when doubled.
                if (c == '|')
                {
                    if (i + 1 >= selector.Length || selector[i + 1] != '|')
                        continue;
                    i++;
                }
                start = i + 1;
            }
        }

        return start;
    }

    /// <summary>
    /// Inserts the scope marker into one compound, just after its type selector so it precedes
    /// any pseudo-element (the renderer's matcher strips everything from <c>::</c> onwards, so an
    /// appended marker would be discarded).
    /// </summary>
    private static string ScopeCompound(string compound, string token)
    {
        if (compound.Length == 0)
            return compound;

        // `:host`/`:host-context` address the host, which is not in this tree; `::slotted`/
        // `::part` address light-DOM nodes. Neither may be narrowed to tree membership.
        if (ContainsKeyword(compound, ":host") ||
            ContainsKeyword(compound, "::slotted") ||
            ContainsKeyword(compound, "::part"))
            return compound;

        var pos = TypeSelectorEnd(compound);
        return compound[..pos] + ScopeAttrSelector(token) + compound[pos..];
    }

    /// <summary>The index just past a compound's leading type selector (0 when it has none).</summary>
    private static int TypeSelectorEnd(string compound)
    {
        var pos = 0;

        if (compound[0] == '*')
            pos = 1;
        else if (IsIdentChar(compound[0]) && compound[0] != '-')
            pos = ConsumeIdent(compound, 0);
        else if (compound[0] != '|')
            return 0;

        // A namespace prefix: `ns|type`, `*|type`, `|type`.
        if (pos < compound.Length && compound[pos] == '|' &&
            (pos + 1 >= compound.Length || compound[pos + 1] != '='))
        {
            pos++;
            if (pos < compound.Length && compound[pos] == '*')
                pos++;
            else if (pos < compound.Length && IsIdentChar(compound[pos]))
                pos = ConsumeIdent(compound, pos);
        }

        return pos;
    }

    private static int ConsumeIdent(string text, int i)
    {
        while (i < text.Length && (IsIdentChar(text[i]) || text[i] == '\\'))
        {
            if (text[i] == '\\' && i + 1 < text.Length)
                i++;
            i++;
        }
        return i;
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
