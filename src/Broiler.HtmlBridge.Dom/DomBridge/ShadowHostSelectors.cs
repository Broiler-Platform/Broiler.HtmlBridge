using System.Globalization;
using System.Linq;
using Broiler.Dom;
using static Broiler.HtmlBridge.DomBridgeUtils;

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
}
