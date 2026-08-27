using Broiler.JavaScript.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The DOM <c>Node</c> child-mutation methods — <c>insertBefore</c>, <c>appendChild</c>, <c>append</c>,
/// <c>prepend</c>, <c>removeChild</c> and <c>replaceChild</c> — registered on every element wrapper,
/// co-located as an HtmlBridge feature module (Phase 3). Pure canonical tree mutation: it resolves the
/// child wrapper(s), enforces the <c>HierarchyRequestError</c> circular-reference guard, and positions
/// or detaches nodes through the bridge's neutral static tree helpers (<c>ParentEl</c>, <c>ChildAt</c>,
/// <c>ChildIndexOf</c>, <c>RemoveNthChild</c>, <c>RemoveChildFrom</c>, <c>SetParent</c>) while driving the
/// side-effecting insertion plus the style-scope invalidation and node-iterator / mutation-observer
/// notifications through the <see cref="ITreeMutationHost"/> contract. Was the bridge's
/// <c>JsJsObjectsInsertBefore080Core</c>, <c>AppendChild088Core</c>, <c>Append089Core</c>,
/// <c>Prepend090Core</c>, <c>RemoveChild091Core</c> and <c>ReplaceChild092Core</c> callbacks.
/// </summary>
internal static class TreeMutationBinding
{
    /// <summary>
    /// Throws the <c>NotFoundError</c> <c>DOMException</c> that the pre-insert, pre-remove and replace
    /// steps all require when the named child is not a child of this parent (DOM §4.2.3).
    /// </summary>
    /// <remarks>
    /// The circular-reference guard beside these call sites already minted a real
    /// <c>HierarchyRequestError</c> through <c>DomBridge.ThrowDOMException</c>, so the machinery was
    /// present and only the not-found branches were missing it — one throwing a plain error whose
    /// message merely began with the name, the other two returning as if they had succeeded.
    /// </remarks>
    private static void ThrowNotFoundError(ITreeMutationHost host, string method, string detail)
    {
        var message = $"Failed to execute '{method}' on 'Node': {detail}";

        if (host.JsContext is { } context)
            DomBridge.ThrowDOMException(context, message, "NotFoundError");

        // Only before the bridge is attached, when there is no realm to mint a DOMException in.
        throw new JSException(message);
    }

    /// <summary>
    /// The DOM <c>Node.moveBefore(node, child)</c> method: repositions an already-attached node
    /// atomically, preserving state that a remove-then-insert would destroy (iframe content,
    /// focus, running animations, render-blocking status).
    /// <para>
    /// Unlike <see cref="InsertBefore"/> this throws rather than quietly returning when the
    /// arguments are wrong: <c>moveBefore</c> is specified to reject a node that is not already in
    /// the tree, or that does not share the target's root, and a caller relying on the atomic
    /// guarantee needs to hear about it rather than silently get a copy-shaped result. The
    /// canonical <c>DomNode.MoveBefore</c> raises those as DOM exceptions.
    /// </para>
    /// <para>
    /// WPT issue #1491 problem 27: without this method the test's script threw on an undefined
    /// function, so the document was never styled and rendered white against Chromium's green.
    /// </para>
    /// </summary>
    public static JSValue MoveBefore(ITreeMutationHost host, DomElement element, in Arguments a)
    {
        if (a.Length == 0 || a[0] is not JSObject movedObj)
            throw new JSException("TypeError: moveBefore requires a node to move.");

        var moved = host.FindDomNodeByJSObject(movedObj);
        if (moved is null)
            throw new JSException("TypeError: moveBefore's first argument is not a node.");

        DomNode? reference = null;
        if (a.Length > 1 && !a[1].IsNull && !a[1].IsUndefined)
        {
            if (a[1] is not JSObject referenceObj)
                throw new JSException("TypeError: moveBefore's second argument is not a node.");

            reference = host.FindDomNodeByJSObject(referenceObj);
            if (reference is null)
                throw new JSException("TypeError: moveBefore's second argument is not a node.");
        }

        host.MoveNodeBefore(element, moved, reference);
        return a[0];
    }

    public static JSValue InsertBefore(ITreeMutationHost host, DomElement element, in Arguments a)
    {
        if (a.Length == 0)
            return JSUndefined.Value;
        if (a[0] is not JSObject newChildObj)
            return JSUndefined.Value;
        var newEl = host.FindDomNodeByJSObject(newChildObj);
        if (newEl == null)
            return a[0];
        // Prevent circular references (HierarchyRequestError per DOM spec)
        if (ReferenceEquals(newEl, element) || element.IsDescendantOf(newEl))
            DomBridge.ThrowDOMException(host.JsContext!, "The new child element contains the parent.", "HierarchyRequestError");
        if (a.Length < 2 || a[1].IsNull || a[1].IsUndefined)
        {
            host.InsertNodeAt(element, newEl, element.ChildNodes.Count);
            return a[0];
        }

        if (a[1] is not JSObject refChildObj)
            return a[0];
        var refEl = host.FindDomNodeByJSObject(refChildObj);
        if (refEl == null)
            return a[0];
        if (ReferenceEquals(newEl, refEl))
            return a[0];
        var idx = DomBridge.ChildIndexOf(element, refEl);
        if (idx < 0)
        {
            // DOM §4.2.3 pre-insert: "If child is non-null and its parent is not parent, then throw a
            // NotFoundError DOMException." This threw a plain error whose message merely BEGAN with the
            // name, so `e instanceof DOMException` was false, `e.name` was "Error" and `e.code` was 0 —
            // the two things a caller tests. The HierarchyRequestError a few lines above was already
            // minted properly through the same helper; this one simply was not reaching it.
            ThrowNotFoundError(host, "insertBefore",
                "The node before which the new node is to be inserted is not a child of this node.");
        }
        host.InsertNodeAt(element, newEl, idx);
        return a[0];
    }

    public static JSValue AppendChild(ITreeMutationHost host, DomElement element, in Arguments a)
    {
        if (a.Length == 0)
            return JSUndefined.Value;
        if (a[0] is not JSObject childObj)
            return JSUndefined.Value;
        // Find the Broiler.Dom.DomElement for this child JSObject
        var childEl = host.FindDomNodeByJSObject(childObj);
        if (childEl == null)
            return a[0];
        // Prevent circular references (HierarchyRequestError per DOM spec)
        if (ReferenceEquals(childEl, element) || element.IsDescendantOf(childEl))
            DomBridge.ThrowDOMException(host.JsContext!, "The new child element contains the parent.", "HierarchyRequestError");
        host.InsertNodeAt(element, childEl, element.ChildNodes.Count);
        return a[0];
    }

    public static JSValue Append(ITreeMutationHost host, DomElement element, in Arguments a)
    {
        if (a.Length == 0)
            return JSUndefined.Value;
        var nodes = host.BuildChildNodeArgumentNodes(a);
        var insertIndex = element.ChildNodes.Count;
        foreach (var node in nodes)
            host.InsertNodeAt(element, node, insertIndex++);
        return JSUndefined.Value;
    }

    public static JSValue Prepend(ITreeMutationHost host, DomElement element, in Arguments a)
    {
        if (a.Length == 0)
            return JSUndefined.Value;
        var nodes = host.BuildChildNodeArgumentNodes(a);
        var insertIndex = 0;
        foreach (var node in nodes)
            host.InsertNodeAt(element, node, insertIndex++);
        return JSUndefined.Value;
    }

    /// <summary>
    /// DOM §4.2.6 <c>ParentNode.replaceChildren()</c> on an element: remove every existing child,
    /// then insert the arguments. Called with none, it empties the element.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The wrapper bound <c>append</c> and <c>prepend</c> but not this third member of the same mixin,
    /// so <c>container.replaceChildren()</c> — the modern way to empty a node, and the reason most
    /// pages reach for it — threw on an undefined function. The document's counterpart has been here
    /// since the mixin was bound there (<c>NodeMutationBinding.ReplaceChildren</c>); this is its
    /// element half, and it mirrors it step for step.
    /// </para>
    /// <para>
    /// The arguments are resolved before anything is removed, because one of them may be a current
    /// child: clearing first would detach and re-insert it, which reaches the same tree by a different
    /// set of mutation records.
    /// </para>
    /// </remarks>
    public static JSValue ReplaceChildren(ITreeMutationHost host, DomElement element, in Arguments a)
    {
        var nodes = a.Length == 0 ? [] : host.BuildChildNodeArgumentNodes(a);

        for (var index = element.ChildNodes.Count - 1; index >= 0; index--)
        {
            var child = DomBridge.ChildAt(element, index);
            host.NotifyNodeIteratorPreRemoval(child);
            DomBridge.RemoveNthChild(element, index);
            DomBridge.SetParent(child, null);
            host.NotifyChildRemoved(element, child, index, null, null);
        }

        host.InvalidateStyleScope(element);

        var insertIndex = 0;
        foreach (var node in nodes)
            host.InsertNodeAt(element, node, insertIndex++);

        return JSUndefined.Value;
    }

    public static JSValue RemoveChild(ITreeMutationHost host, DomElement element, in Arguments a)
    {
        if (a.Length == 0)
            return JSUndefined.Value;
        if (a[0] is not JSObject childObj)
            return JSUndefined.Value;
        var childEl = host.FindDomNodeByJSObject(childObj);
        if (childEl == null)
            return a[0];
        var idx = DomBridge.ChildIndexOf(element, childEl);
        if (idx < 0)
        {
            // DOM §4.2.3 pre-remove: "If child's parent is not parent, then throw a NotFoundError
            // DOMException." This used to return the node unchanged, which is the worst shape a
            // failure can take: `removeChild` returns the removed node on success, so returning it
            // here told the caller the removal had happened. Code that removes a node and then
            // re-parents the returned value silently operated on a node still attached to its
            // original parent.
            ThrowNotFoundError(host, "removeChild",
                "The node to be removed is not a child of this node.");
        }
        host.NotifyNodeIteratorPreRemoval(childEl);
        DomBridge.RemoveNthChild(element, idx);
        DomBridge.SetParent(childEl, null);
        host.InvalidateStyleScope(element);
        host.NotifyChildRemoved(element, childEl, idx, null, null);
        return a[0];
    }

    public static JSValue ReplaceChild(ITreeMutationHost host, DomElement element, in Arguments a)
    {
        if (a.Length < 2)
            return JSUndefined.Value;
        if (a[0] is not JSObject newChildObj || a[1] is not JSObject oldChildObj)
            return JSUndefined.Value;
        var newEl = host.FindDomNodeByJSObject(newChildObj);
        var oldEl = host.FindDomNodeByJSObject(oldChildObj);
        if (newEl == null || oldEl == null)
            return a[1];
        // Prevent circular references (HierarchyRequestError per DOM spec)
        if (ReferenceEquals(newEl, element) || element.IsDescendantOf(newEl))
            DomBridge.ThrowDOMException(host.JsContext!, "The new child element contains the parent.", "HierarchyRequestError");
        var idx = DomBridge.ChildIndexOf(element, oldEl);
        if (idx < 0)
        {
            // Same rule for replaceChild (DOM §4.2.3 replace: "If child's parent is not parent, then
            // throw a NotFoundError DOMException"), and the same misleading shape — it returned
            // a[1], which is what a successful replaceChild returns. This check is before any
            // mutation, which is where the specification puts the validation; the defensive re-check
            // further down runs after newEl has already been detached, so it keeps returning rather
            // than throwing out of a half-finished mutation.
            ThrowNotFoundError(host, "replaceChild",
                "The node to be replaced is not a child of this node.");
        }
        var previousSibling = idx > 0 ? DomBridge.ChildAt(element, idx - 1) : null;
        var nextSibling = idx + 1 < element.ChildNodes.Count ? DomBridge.ChildAt(element, idx + 1) : null;
        // If newChild is already in this parent, remove it first and re-find idx
        if (ReferenceEquals(DomBridge.ParentEl(newEl), element))
        {
            DomBridge.RemoveChildFrom(element, newEl);
            idx = DomBridge.ChildIndexOf(element, oldEl);
            if (idx < 0)
                return a[1];
        }
        else
        {
            if (DomBridge.ParentEl(newEl) != null)
            {
                var oldParent = DomBridge.ParentEl(newEl);
                var oldIndex = DomBridge.ChildIndexOf(oldParent, newEl);
                if (oldIndex >= 0)
                {
                    host.NotifyNodeIteratorPreRemoval(newEl);
                    DomBridge.RemoveNthChild(oldParent, oldIndex);
                    host.NotifyChildRemoved(oldParent, newEl, oldIndex, null, null);
                }
            }
        }

        // Single canonical replace: ReplaceChild removes oldEl and inserts newEl at its exact
        // position, firing one ChildList(removed oldEl) + one ChildList(added newEl). The prior
        // detach-oldEl + append-newEl-at-end + ReplaceChild(ChildNodes[idx]) dance fired several
        // spurious canonical records that the NodeIterator/CSS mutation subscribers observe. newEl
        // was already detached from any prior parent above; oldEl is still a child of element here.
        element.ReplaceChild(newEl, oldEl);
        host.InvalidateStyleScope(element);
        host.NotifyChildRemoved(element, oldEl, idx, previousSibling, nextSibling);
        host.NotifyChildAdded(element, newEl, idx);
        return a[1]; // returns the old child
    }
}
