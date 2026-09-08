using Broiler.HtmlBridge.Jseal;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// DOM traversal APIs — <c>TreeWalker</c>, <c>NodeIterator</c>,
/// <c>Range</c>, and the node-filter machinery.
/// </summary>
public sealed partial class DomBridge
{
    // -------- TreeWalker, NodeIterator, Range builders (Phase 3: extracted) --------
    // The TreeWalker/NodeIterator/Range construction and every Range callback now live in the
    // co-located Broiler.HtmlBridge.Dom.Features.TraversalBinding feature module; these thin
    // wrappers keep the historical call sites (createTreeWalker/createNodeIterator/createRange in
    // Registration and sub-document registration) source-compatible.
    //
    // The module is migrated to JSEAL and these three are its remaining engine-typed edge: their
    // caller, DomBridge.SubDocumentHost.cs, implements ISubDocumentHost — another group's file this
    // round — and still hands JS objects around as engine values. Dom.Runtime.JsInterop is the cast
    // between the two, not a conversion: the handle carries the engine's own object, so wrapper
    // identity is unchanged. They lose their engine types when that host contract migrates.

    private Broiler.JavaScript.Runtime.JSObject BuildTreeWalker(DomElement root, int whatToShow, Broiler.JavaScript.BuiltIns.Function.JSFunction? filterFn) =>
        Dom.Runtime.JsInterop.ToEngineObject(
            _traversal.BuildTreeWalker(root, whatToShow, ToFilterHandle(filterFn)));

    private Broiler.JavaScript.Runtime.JSObject BuildNodeIterator(DomElement root, int whatToShow, Broiler.JavaScript.BuiltIns.Function.JSFunction? filterFn) =>
        Dom.Runtime.JsInterop.ToEngineObject(
            _traversal.BuildNodeIterator(root, whatToShow, ToFilterHandle(filterFn)));

    private Broiler.JavaScript.Runtime.JSObject BuildRange(DomNode? documentRoot = null) =>
        Dom.Runtime.JsInterop.ToEngineObject(_traversal.BuildRange(documentRoot));

    /// <summary>
    /// A <c>NodeFilter</c> callback as a JSEAL handle. No filter is
    /// <see cref="JsValue.Missing"/>, which is the value the module tests for callability against —
    /// the same question the null check asked here.
    /// </summary>
    private static JsValue ToFilterHandle(Broiler.JavaScript.BuiltIns.Function.JSFunction? filterFn) =>
        filterFn is null ? JsValue.Missing : Dom.Runtime.JsInterop.FromEngineObject(filterFn);

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

    /// <summary>
    /// Returns the top-level nodes overlapping the given range boundaries, for the range
    /// client-rect geometry. For a single container this is the children between the offsets;
    /// across containers it is the document-order nodes strictly between start and end, keeping
    /// only those not already covered by an included ancestor. This is a client-rect geometry
    /// heuristic — it includes partially-overlapping elements, unlike the spec-strict
    /// <c>DomRange.IsContained</c> set — so it stays bridge-owned rather than promoting to
    /// canonical Broiler.Dom. It reuses canonical <see cref="DomNode.InclusiveDescendants"/> for
    /// the document-order walk.
    /// </summary>
    internal static List<DomNode> GetNodesInRange(DomNode startContainer, int startOffset, DomNode endContainer, int endOffset)
    {
        var result = new List<DomNode>();
        if (ReferenceEquals(startContainer, endContainer))
        {
            // Same container — return children between offsets
            for (var i = startOffset; i < Math.Min(endOffset, startContainer.ChildNodes.Count); i++)
                result.Add(ChildAt(startContainer, i));
            return result;
        }

        // Different containers — collect nodes between start and end
        var ancestor = startContainer.CommonAncestorWith(endContainer);
        if (ancestor == null) return result;

        var allNodes = ancestor.InclusiveDescendants().ToList();
        var startIdx = allNodes.IndexOf(startContainer);
        var endIdx = allNodes.IndexOf(endContainer);
        if (startIdx < 0 || endIdx < 0) return result;

        for (var i = startIdx + 1; i < endIdx; i++)
        {
            var node = allNodes[i];
            // Only include top-level nodes (not descendants of already-included nodes)
            var isDescendantOfIncluded = result.Any(r => node.IsDescendantOf(r));
            if (!isDescendantOfIncluded)
                result.Add(node);
        }
        return result;
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

    private static void UpdateCharacterData(DomNode target, string? newValue) => SetBridgeText(target, newValue ?? string.Empty);

    // The canonical DomCharacterData.Data setter publishes a CharacterData record to
    // DomDocument.Mutated (with its own value-changed guard), so the observer subscription delivers
    // it — no explicit notify needed.
    private void SetCharacterData(DomNode target, string? newValue) => UpdateCharacterData(target, newValue);

    private void NotifyNodeIteratorPreRemoval(DomNode nodeToBeRemoved) { }
}
