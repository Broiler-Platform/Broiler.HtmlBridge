using System.Text.RegularExpressions;
// No engine namespace at all. The two wrapper-cache reverse lookups below take a JSEAL handle, which
// is what all fifteen of their callers already held; they stopped unwrapping it at their own seams in
// the same commit that re-typed these, and the using that comment described went with them.
using Broiler.HtmlBridge.Jseal;
using Broiler.Dom;
using Broiler.CSS;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// Internal helper methods — string conversions, DOM tree utilities,
/// form-control collection, table-row helpers, and JS-object builders
/// for <c>style</c>, <c>classList</c>, <c>localStorage</c>, and
/// <c>canvas.getContext("2d")</c>.
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>
    /// Parses a DOCTYPE declaration and creates the canonical <see cref="DomDocumentType"/> node.
    /// </summary>
    private DomDocumentType? ParseDocType(string html)
    {
        var match = DocTypePattern.Match(html);
        if (!match.Success) return null;

        var name = match.Groups[1].Value;
        var publicId = match.Groups[2].Success ? match.Groups[2].Value : string.Empty;
        var systemId = match.Groups[3].Success ? match.Groups[3].Value : string.Empty;

        return CreateBridgeDocumentType(name, publicId, systemId);
    }

    /// <summary>
    /// Finds the <see cref="DomElement"/> a wrapper handle stands for, or <see langword="null"/>
    /// when it stands for none — a handle that is not an object included.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The parameter is the handle every caller already held.</b> Fifteen files across the
    /// assembly call this pair, and each of them used to unwrap its handle at its own seam only for
    /// this pair to wrap it straight back up before asking
    /// <see cref="Dom.Runtime.JsObjectRegistry"/> — which has been keyed on
    /// <see cref="JsValue.ObjectIdentity"/> since it was re-typed, so the round trip resolved to the
    /// same lookup it started from. The <em>names</em> still spell the type they no longer take;
    /// renaming them is a separate change, and nothing the neutrality ratchet measures depends on it,
    /// because that check counts the engine's namespace as text and a method name spells none.
    /// </para>
    /// <para>
    /// <b>A handle that is not an object answers <see langword="null"/> rather than throwing.</b>
    /// The registry treats a non-object wrapper as simply absent from the map, so the
    /// <c>IsObject</c> test that ten of the call sites still put in front of these is now the same
    /// question this asks and is redundant rather than load-bearing. Collapsing those is a separate
    /// change; it would also turn <c>JsObjects.NonElementNodes.cs</c>'s <c>NodeForWrapper</c> into a
    /// bare alias and pull its six call sites in with it.
    /// </para>
    /// </remarks>
    private DomElement? FindDomElementByJSObject(JsValue wrapper) =>
        FindDomNodeByJSObject(wrapper) as DomElement;

    /// <summary>
    /// Finds the canonical <see cref="DomNode"/> a wrapper handle stands for, in constant time
    /// (the reverse map is a hash lookup, not the scan this used to describe). Unlike
    /// <see cref="FindDomElementByJSObject"/> this also resolves text/comment nodes
    /// (RF-BRIDGE-1c Phase F), which ranges need now that they carry canonical char-data nodes.
    /// </summary>
    /// <inheritdoc cref="FindDomElementByJSObject" path="/remarks" />
    private DomNode? FindDomNodeByJSObject(JsValue wrapper) =>
        _jsObjects.TryGetNode(wrapper, out var node) ? node : null;

    // Phase 4 item 5: the bridge's IsDescendant(ancestor, candidate) copy is deleted; call sites use
    // the canonical Broiler.Dom.DomNode.IsDescendantOf(ancestor) instance method (identical ancestor
    // walk; every bridge call site passes a non-null ancestor, so canonical's null-ancestor throw is
    // unreachable).

    /// <summary>
    /// Clones a <see cref="DomElement"/>. When <paramref name="deep"/> is true,
    /// all descendants are recursively cloned.
    /// </summary>
    private DomNode CloneDomElement(DomNode source, bool deep)
    {
        // Phase 4 item 5: delegate the tree + attribute clone to canonical DomNode.CloneNode (spec §4.4)
        // instead of the bridge's hand-rolled per-node-kind rebuild. Canonical CloneShallow handles every
        // node kind — element (namespace + attribute set verbatim), text/comment (data), doctype
        // (name/publicId/systemId) and fragment — and, when deep, recurses the child list in order. This
        // replaces the former CreateBridgeElementNS-from-main-`_document` construction + SetAttribute
        // attribute copy; canonical preserves the source's owner document and attribute keys verbatim,
        // which is spec-correct (the old path minted every clone from the main document and lowercased
        // no-namespace attribute names). Cloning does not mutate the live document, so there is no
        // MutationObserver/NodeIterator/live-range side-effect coupling here (unlike Normalize).
        var clone = source.CloneNode(deep);

        // Canonical CloneNode knows nothing about the bridge's parallel per-element runtime state (inline
        // style + baked overlay, form control, scroll, dialog/popover, shadow, stylesheet, document
        // viewport, animation, position-area memo), so copy it onto every element in the cloned subtree
        // through the single CopyBridgeRuntimeStateTo authority (P4.13 / P4.14-inc3).
        CopyRuntimeStateForClonedSubtree(source, clone, deep);
        return clone;
    }

    /// <summary>
    /// Walks a source subtree and its canonical <c>CloneNode</c> copy in lockstep, copying the bridge's
    /// per-element runtime state (via <see cref="CopyBridgeRuntimeStateTo"/>) from each source element to
    /// its clone. Canonical <c>CloneNode(deep)</c> reproduces the child list in order, so index <c>i</c>
    /// of the source's children pairs with index <c>i</c> of the clone's.
    /// </summary>
    private void CopyRuntimeStateForClonedSubtree(DomNode source, DomNode clone, bool deep)
    {
        if (source is DomElement sourceElement && clone is DomElement cloneElement)
            CopyBridgeRuntimeStateTo(sourceElement, cloneElement);

        if (!deep)
            return;

        var sourceChildren = source.ChildNodes;
        var cloneChildren = clone.ChildNodes;
        for (var i = 0; i < sourceChildren.Count && i < cloneChildren.Count; i++)
            CopyRuntimeStateForClonedSubtree(sourceChildren[i], cloneChildren[i], true);
    }

    /// <summary>
    /// Phase 4 item 5 (CloneDomElement de-risk): the single authority that copies every bridge
    /// per-element runtime-state table from a source element onto its <c>cloneNode</c> copy.
    /// Canonical <c>DomNode.CloneNode</c> clones the tree and attributes but knows nothing about
    /// the bridge's parallel per-element state (inline style, form control, scroll, dialog/popover
    /// top layer, shadow linkage, stylesheet CSSOM, document viewport flag, animation timeline and
    /// the position-area memo), so the bridge carries it here. Consolidating it out of the scattered
    /// inline block in <see cref="CloneDomElement"/> means each state table owns its own
    /// <c>CopyTo</c> (in <c>RuntimeStates.cs</c>, next to its fields) — a new field/table can no
    /// longer be silently dropped from clones — and isolates the exact bridge-state copy the
    /// eventual canonical-<c>CloneNode</c> swap (item 5) will call alongside the canonical clone.
    /// </summary>
    private void CopyBridgeRuntimeStateTo(DomElement source, DomElement clone)
    {
        // Inline style (RF-BRIDGE-1c Phase B): copy the source's live style dict — which may hold
        // JS `element.style` mutations not yet synced to the `style=` attribute — over the clone's
        // lazily-seeded attribute values. `InlineStyle(clone)` seeds from the (already-copied)
        // `style=` attribute before the Clear, so the copy is authoritative.
        var cloneStyle = InlineStyle(clone);
        cloneStyle.Clear();
        foreach (var kv in InlineStyle(source))
            cloneStyle[kv.Key] = kv.Value;

        // Per-bridge instance tables (Phase 2 items 3/4 de-globalization) — each owns its CopyTo.
        FormControlStateFor(source).CopyTo(FormControlStateFor(clone));
        ScrollStateFor(source).CopyTo(ScrollStateFor(clone));
        DialogStateFor(source).CopyTo(DialogStateFor(clone));
        ShadowStateFor(source).CopyTo(ShadowStateFor(clone));
        StyleSheetStateFor(source).CopyTo(StyleSheetStateFor(clone));
        DocumentStateFor(source).CopyTo(DocumentStateFor(clone));
        AnimationStateFor(source).CopyTo(AnimationStateFor(clone));

        // Memoized position-area resolution (was ElementRuntimeState.Layout, now the bridge-level
        // PositionAreaResolutions cache — see PositionAreaQueries.cs).
        CopyPositionAreaResolution(source, clone);

        // Baked-style overlay (Phase 4 item 2 increment 3): serialize-time bakes now live off the
        // inline-style dict, so copy the overlay too. A no-op unless the source was cloned after baking.
        CopyBakedStyleOverlay(source, clone);

        // Web Animations targeting a pseudo-element bake outside the inline style (a pseudo has no
        // node); the serialization pass reads them off the projected element, so they travel here.
        CopyAnimatedPseudoStyles(source, clone);
    }

    /// <summary>
    /// Recursively unchecks all radio inputs with the given name within the scope,
    /// except for the specified element. Used for radio button mutual exclusion.
    /// </summary>
    internal void UncheckRadioSiblings(DomElement scope, DomElement except, string radioName)
    {
        foreach (var child in ChildElements(scope))
        {
            if (!IsText(child) && !ReferenceEquals(child, except))
            {
                if (string.Equals(child.TagName, "input", StringComparison.OrdinalIgnoreCase) &&
                    TryGetAttribute(child, "type", out var st) &&
                    string.Equals(st, "radio", StringComparison.OrdinalIgnoreCase) &&
                    TryGetAttribute(child, "name", out var sn) &&
                    string.Equals(sn, radioName, StringComparison.Ordinal))
                {
                    FormControlStateFor(child).Checked.Set(false);
                }

                UncheckRadioSiblings(child, except, radioName);
            }
        }
    }

    // form.elements collection (indexed + named access) moved to the Phase 3 FormBinding feature
    // module (Broiler.HtmlBridge.Dom.Features).

    // classList / DOMTokenList moved to the Phase 3 ClassListBinding feature module
    // (Broiler.HtmlBridge.Dom.Features).
    // style / CSSStyleDeclaration (element.style, rule.style, getComputedStyle result) moved to the
    // Phase 3 (P3.14) StyleDeclarationBinding feature module (Broiler.HtmlBridge.Dom.Features).
    // canvas.getContext("2d") + BuildCanvas2DContext moved to the Phase 3 (P3.64) CanvasBinding feature
    // module (Broiler.HtmlBridge.Dom.Features), unblocked once Phase 6/P8.9 dissolved
    // Broiler.HtmlBridge.Rendering into Dom.

}
