using System.Globalization;
using System.Linq;
using Broiler.Dom;
using static Broiler.HtmlBridge.DomBridgeUtils;

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
}
