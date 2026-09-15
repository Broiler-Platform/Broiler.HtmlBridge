using Broiler.HtmlBridge.Jseal;
using Broiler.Dom;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// DOM traversal APIs — <c>TreeWalker</c>, <c>NodeIterator</c>,
/// <c>Range</c>, and the node-filter machinery.
/// </summary>
public sealed partial class DomBridge
{
    // -------- TreeWalker, NodeIterator, Range builders (Phase 3: extracted) --------
    // The TreeWalker/NodeIterator/Range construction and every Range callback live in the co-located
    // Broiler.HtmlBridge.Dom.Features.TraversalBinding feature module, which is migrated to JSEAL.
    //
    // The three engine-typed wrappers that used to stand here are gone: their last caller,
    // DomBridge.SubDocumentHost.cs, now asks _traversal for a handle directly, so there was nothing
    // left for them to convert for. Every builder call site — createRange/createTreeWalker/
    // createNodeIterator on the main document and on a sub-document — goes to the module.

    // Phase 4 items 4/5 (P4.10 follow-up): the bridge's FindCommonAncestor copy was deleted after its
    // promotion to canonical Broiler.Dom.DomNode.CommonAncestorWith landed in the pinned submodule
    // (patches/0002, applied by the maintainer). Call sites use a.CommonAncestorWith(b), which is
    // null-tolerant and returns null for nodes in different trees — matching the deleted helper.

    /// <summary>
    /// A CSSOM-View <c>DOMRect</c>-shaped object over a used-value rectangle, for
    /// <c>Range.getBoundingClientRect</c> and <c>Range.getClientRects</c>.
    /// </summary>
    /// <remarks>
    /// Eight data properties, enumerable, configurable and writable — the shape this has always
    /// installed. A real <c>DOMRect</c> carries them on its prototype as accessors; that is a
    /// separate change from this migration, and making it here would alter what
    /// <c>Object.getOwnPropertyNames</c> of a rect answers.
    /// </remarks>
    private JsValue CreateDomRectObject((double Left, double Top, double Width, double Height) rectData)
    {
        var rect = Realm.NewObject();
        Realm.DefineValue(rect, "x", JsValue.Number(rectData.Left));
        Realm.DefineValue(rect, "y", JsValue.Number(rectData.Top));
        Realm.DefineValue(rect, "top", JsValue.Number(rectData.Top));
        Realm.DefineValue(rect, "left", JsValue.Number(rectData.Left));
        Realm.DefineValue(rect, "right", JsValue.Number(rectData.Left + rectData.Width));
        Realm.DefineValue(rect, "bottom", JsValue.Number(rectData.Top + rectData.Height));
        Realm.DefineValue(rect, "width", JsValue.Number(rectData.Width));
        Realm.DefineValue(rect, "height", JsValue.Number(rectData.Height));
        return rect;
    }

    private List<(double Left, double Top, double Width, double Height)> GetClientRectsForRange(DomRange state)
    {
        var rects = new List<(double Left, double Top, double Width, double Height)>();
        if (state.Collapsed)
            return rects;

        foreach (var node in GetNodesInRange(state.StartContainer, state.StartOffset, state.EndContainer, state.EndOffset))
            CollectClientRectsForRangeNode(node, rects);

        return rects;
    }

    private void CollectClientRectsForRangeNode(DomNode node, List<(double Left, double Top, double Width, double Height)> rects)
    {
        // Character-data nodes contribute no client rect here (their text runs are measured
        // elsewhere); after this guard the node is an element.
        if (IsText(node) || IsComment(node) || node is not DomElement element)
            return;

        var display = GetComputedProps(element).GetValueOrDefault("display");
        if (string.Equals(display, "contents", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var child in ChildElements(element))
                CollectClientRectsForRangeNode(child, rects);

            return;
        }

        var rect = GetBoundingClientRectForDomElement(element, isRoot: false);
        if (rect.Width > 0 || rect.Height > 0)
            rects.Add(rect);
    }

    // Mutation-delivery consolidation (Phase 4): the bridge no longer pushes MutationObserver,
    // Range, or NodeIterator notifications from its mutation path. Delivery is driven off canonical
    // DomDocument.Mutated — MutationObserverBinding subscribes per observed document, and canonical
    // DomRange (trackMutations) / DomNodeIterator self-subscribe. Because the step-1 primitive
    // cleanup makes each logical op fire exactly one canonical record, the subscribers see the same
    // record stream the explicit channel produced. These former Notify* seams are kept as no-ops so
    // the ~35 historical mutation-path call sites (child add/remove, attribute writes, the
    // NodeIterator pre-removal marker) need no edit.
    private void NotifyChildAdded(DomNode parent, DomNode addedChild, int index) { }

    private void NotifyChildRemoved(DomNode parent, DomNode removedChild, int index, DomNode? previousSibling = null, DomNode? nextSibling = null) { }

    private void NotifyAttributeMutationObservers(DomElement target, string attributeName, string? oldValue) { }

    // The canonical DomCharacterData.Data setter publishes a CharacterData record to
    // DomDocument.Mutated (with its own value-changed guard), so the observer subscription delivers
    // it — no explicit notify needed.
    private void SetCharacterData(DomNode target, string? newValue) => UpdateCharacterData(target, newValue);

    private void NotifyNodeIteratorPreRemoval(DomNode nodeToBeRemoved) { }
}
