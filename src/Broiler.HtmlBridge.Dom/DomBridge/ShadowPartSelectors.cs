using System.Linq;
using Broiler.Dom;
using static Broiler.HtmlBridge.DomBridgeUtils;

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
}
