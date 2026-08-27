using System.Linq;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// Render-time flattening of a shadow host's light-DOM children (DOM §4.2.2 "slottable" /
/// CSS Scoping 1 §3.3).
/// <para>
/// A host's light-DOM children do not render in their own right: they render only where a
/// <c>&lt;slot&gt;</c> in the shadow tree assigns them. The serializer emits the host's light
/// children <em>and</em> its <c>#shadow-root</c> side by side, so a shadow tree with no
/// <c>&lt;slot&gt;</c> at all still painted the light children — the WPT
/// <c>css/css-shadow</c> family's standard "FAIL" text, which the references never show
/// (<c>css-scoping-shadow-root-hides-children</c>, and the leftover text inside the green box
/// of <c>css-scoping-shadow-host-functional-rule</c>).
/// </para>
/// <para>
/// This handles the unambiguous half: when the shadow tree exposes no slot, nothing can be
/// assigned, so the host's light children generate no boxes and are dropped from the
/// render-bound tree. A shadow tree that <em>does</em> contain a slot is left alone — placing
/// each child at its assigned slot is full slot flattening, which this does not attempt.
/// Only the render-bound serialization is affected; the live DOM and JS-visible
/// <c>innerHTML</c> still expose the light children.
/// </para>
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>
    /// Drops the light-DOM children of every shadow host whose shadow tree contains no
    /// <c>&lt;slot&gt;</c>. A no-op for a document with no shadow roots.
    /// </summary>
    private void HideUnslottedShadowHostChildren(DomElement root)
    {
        var shadowRoots = root.Descendants()
            .OfType<DomElement>()
            .Where(e => string.Equals(e.TagName, "#shadow-root", StringComparison.Ordinal))
            .ToList();

        foreach (var shadowRoot in shadowRoots)
        {
            var host = ParentEl(shadowRoot);
            if (host == null)
                continue;

            // A slot somewhere in this shadow tree can assign the light children — leave them
            // in place rather than dropping content the author expects to see.
            if (ShadowTreeHasSlot(shadowRoot))
                continue;

            foreach (var child in host.ChildNodes.ToArray())
            {
                if (ReferenceEquals(child, shadowRoot))
                    continue;

                host.RemoveChild(child);
            }
        }
    }

    /// <summary>
    /// Replaces each <c>#shadow-root</c> element with its children, in place, for the
    /// render-bound tree.
    /// <para>
    /// <c>#shadow-root</c> is a bridge-internal container, not an HTML element, and it is
    /// serialized literally as <c>&lt;#shadow-root&gt;</c>. Per the HTML tokenizer a <c>&lt;</c>
    /// followed by anything that is not an ASCII letter (or <c>!</c>, <c>/</c>, <c>?</c>) is not a
    /// tag at all — it is emitted as character data — so the renderer painted the literal string
    /// "&lt;#shadow-root&gt;" inside every shadow host. Flattening the wrapper away puts the
    /// shadow content directly in the host, which is what the renderer should draw.
    /// </para>
    /// <para>
    /// Deliberately limited to shadow trees with no <c>&lt;slot&gt;</c> — the same set
    /// <see cref="HideUnslottedShadowHostChildren"/> acts on. Flattening the wrapper moves the
    /// shadow content into the host, which changes the ancestor chain that slot assignment and
    /// scroll-container resolution walk; restricting it to slotless trees leaves every
    /// slot-bearing shadow tree byte-identical to its previous rendering, so this cannot disturb
    /// slot behaviour. Slotless trees have no assignment to preserve, so the flatten is safe
    /// there and is what the <c>css/css-shadow</c> <c>:host</c> reftests need.
    /// </para>
    /// <para>
    /// Runs after the other shadow passes, which locate content by the <c>#shadow-root</c>
    /// wrapper. Only the render-bound serialization is affected.
    /// </para>
    /// </summary>
    private void UnwrapShadowRootsForRender(DomElement root)
    {
        var shadowRoots = root.Descendants()
            .OfType<DomElement>()
            .Where(e => string.Equals(e.TagName, "#shadow-root", StringComparison.Ordinal))
            .Where(e => !ShadowTreeHasSlot(e))
            .ToList();

        // Innermost first, so unwrapping a nested root is not disturbed by its ancestor moving.
        shadowRoots.Reverse();

        foreach (var shadowRoot in shadowRoots)
        {
            var host = ParentEl(shadowRoot);
            if (host == null)
                continue;

            var index = ChildIndexOf(host, shadowRoot);
            if (index < 0)
                continue;

            RemoveNthChild(host, index);
            SetParent(shadowRoot, null);

            foreach (var child in shadowRoot.ChildNodes.ToArray())
            {
                InsertChildAt(host, index, child);
                index++;
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="shadowRoot"/>'s tree contains a <c>&lt;slot&gt;</c>, not
    /// descending into a nested <c>#shadow-root</c> (whose slots assign that root's own host's
    /// children, not this one's).
    /// </summary>
    private bool ShadowTreeHasSlot(DomElement shadowRoot)
    {
        foreach (var child in ChildElements(shadowRoot))
        {
            if (IsText(child))
                continue;

            if (string.Equals(child.TagName, "#shadow-root", StringComparison.Ordinal))
                continue;

            if (child.TagName.Equals("slot", StringComparison.OrdinalIgnoreCase))
                return true;

            if (ShadowTreeHasSlot(child))
                return true;
        }

        return false;
    }
}
