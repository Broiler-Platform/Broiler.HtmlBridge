using Broiler.HtmlBridge.Jseal;
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
/// <remarks>
/// <para>
/// <b>The module's two halves rejoined when its second installer moved.</b> The three
/// <c>ParentNode</c> members are on <c>Element.prototype</c>, installed by
/// <c>DomBridge/ElementInterface.cs</c>; the five <c>Node</c> members stay each wrapper's own property
/// and are installed by <c>DomBridge/JsObjects.cs</c>. Both mint through the realm now, so every body
/// here reads a <see cref="JsCall"/> and the module names no engine type. The script context the
/// contract used to carry went with them: a call frame brings its own realm, and
/// <see cref="IJsCalls.DomError"/> mints the <c>DOMException</c> from that. (The three variadic
/// members below forward <see cref="JsCall"/>'s own span of handles, which is a JSEAL member and not
/// the engine's argument frame, however alike the two read.)
/// </para>
/// <para>
/// <b>Two <c>moveBefore</c> rejections are plain <c>Error</c>s whose message merely begins with
/// <c>TypeError:</c>, and they stay that way.</b> That is what the engine-framed bodies threw —
/// <c>new JSException(string)</c> builds an <c>Error</c>, not the constructor its message names — so
/// <c>e instanceof TypeError</c> is false and <c>e.name</c> is <c>"Error"</c> where WebIDL says
/// otherwise. Reproducing it exactly is what a refactor owes; correcting it is a behaviour change and
/// belongs to whoever makes it deliberately.
/// </para>
/// </remarks>
internal static class TreeMutationBinding
{
    /// <summary>
    /// The <c>NotFoundError</c> <c>DOMException</c> that the pre-insert, pre-remove and replace steps
    /// all require when the named child is not a child of this parent (DOM §4.2.3).
    /// </summary>
    /// <remarks>
    /// The circular-reference guard beside these call sites already minted a real
    /// <c>HierarchyRequestError</c>, so the machinery was present and only the not-found branches were
    /// missing it — one throwing a plain error whose message merely began with the name, the other two
    /// returning as if they had succeeded.
    /// <para>
    /// The "no realm to mint a <c>DOMException</c> in" fallback the engine-framed version carried is
    /// dropped rather than translated: it existed for a bridge not yet attached to a script context,
    /// and a body reached through a <see cref="JsCall"/> is by construction running inside a realm.
    /// </para>
    /// </remarks>
    private static Exception NotFoundError(IJsRealm realm, string method, string detail) =>
        realm.DomError("NotFoundError", $"Failed to execute '{method}' on 'Node': {detail}");

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
    public static JsValue MoveBefore(ITreeMutationHost host, DomElement element, in JsCall call)
    {
        // JsErrorKind.Error with the name inside the message, not JsErrorKind.TypeError: see the note
        // on this class for what these three rejections actually threw and why that is preserved.
        if (call.Length == 0 || !call[0].IsObject)
            throw call.Realm.Error(JsErrorKind.Error, "TypeError: moveBefore requires a node to move.");

        var moved = host.FindNode(call[0]);
        if (moved is null)
            throw call.Realm.Error(JsErrorKind.Error, "TypeError: moveBefore's first argument is not a node.");

        DomNode? reference = null;
        if (call.Length > 1 && !call[1].IsNull && !call[1].IsUndefined)
        {
            if (!call[1].IsObject)
                throw call.Realm.Error(JsErrorKind.Error, "TypeError: moveBefore's second argument is not a node.");

            reference = host.FindNode(call[1]);
            if (reference is null)
                throw call.Realm.Error(JsErrorKind.Error, "TypeError: moveBefore's second argument is not a node.");
        }

        host.MoveNodeBefore(element, moved, reference);
        return call[0];
    }

    public static JsValue InsertBefore(ITreeMutationHost host, DomElement element, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Undefined;
        if (!call[0].IsObject)
            return JsValue.Undefined;
        var newEl = host.FindNode(call[0]);
        if (newEl == null)
            return call[0];
        // Prevent circular references (HierarchyRequestError per DOM spec)
        if (ReferenceEquals(newEl, element) || element.IsDescendantOf(newEl))
            throw call.Realm.DomError("HierarchyRequestError", "The new child element contains the parent.");
        if (call.Length < 2 || call[1].IsNull || call[1].IsUndefined)
        {
            host.InsertNodeAt(element, newEl, element.ChildNodes.Count);
            return call[0];
        }

        if (!call[1].IsObject)
            return call[0];
        var refEl = host.FindNode(call[1]);
        if (refEl == null)
            return call[0];
        if (ReferenceEquals(newEl, refEl))
            return call[0];
        var idx = DomBridge.ChildIndexOf(element, refEl);
        if (idx < 0)
        {
            // DOM §4.2.3 pre-insert: "If child is non-null and its parent is not parent, then throw a
            // NotFoundError DOMException." This threw a plain error whose message merely BEGAN with the
            // name, so `e instanceof DOMException` was false, `e.name` was "Error" and `e.code` was 0 —
            // the two things a caller tests. The HierarchyRequestError a few lines above was already
            // minted properly; this one simply was not reaching the same machinery.
            throw NotFoundError(call.Realm, "insertBefore",
                "The node before which the new node is to be inserted is not a child of this node.");
        }
        host.InsertNodeAt(element, newEl, idx);
        return call[0];
    }

    public static JsValue AppendChild(ITreeMutationHost host, DomElement element, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Undefined;
        if (!call[0].IsObject)
            return JsValue.Undefined;
        // Find the Broiler.Dom.DomElement for this child wrapper
        var childEl = host.FindNode(call[0]);
        if (childEl == null)
            return call[0];
        // Prevent circular references (HierarchyRequestError per DOM spec)
        if (ReferenceEquals(childEl, element) || element.IsDescendantOf(childEl))
            throw call.Realm.DomError("HierarchyRequestError", "The new child element contains the parent.");
        host.InsertNodeAt(element, childEl, element.ChildNodes.Count);
        return call[0];
    }

    public static JsValue Append(ITreeMutationHost host, DomElement element, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Undefined;
        var nodes = host.BuildChildNodeArgumentNodes(call.Arguments);
        var insertIndex = element.ChildNodes.Count;
        foreach (var node in nodes)
            host.InsertNodeAt(element, node, insertIndex++);
        return JsValue.Undefined;
    }

    public static JsValue Prepend(ITreeMutationHost host, DomElement element, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Undefined;
        var nodes = host.BuildChildNodeArgumentNodes(call.Arguments);
        var insertIndex = 0;
        foreach (var node in nodes)
            host.InsertNodeAt(element, node, insertIndex++);
        return JsValue.Undefined;
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
    public static JsValue ReplaceChildren(ITreeMutationHost host, DomElement element, in JsCall call)
    {
        var nodes = call.Length == 0 ? [] : host.BuildChildNodeArgumentNodes(call.Arguments);

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

        return JsValue.Undefined;
    }

    public static JsValue RemoveChild(ITreeMutationHost host, DomElement element, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Undefined;
        if (!call[0].IsObject)
            return JsValue.Undefined;
        var childEl = host.FindNode(call[0]);
        if (childEl == null)
            return call[0];
        var idx = DomBridge.ChildIndexOf(element, childEl);
        if (idx < 0)
        {
            // DOM §4.2.3 pre-remove: "If child's parent is not parent, then throw a NotFoundError
            // DOMException." This used to return the node unchanged, which is the worst shape a
            // failure can take: `removeChild` returns the removed node on success, so returning it
            // here told the caller the removal had happened. Code that removes a node and then
            // re-parents the returned value silently operated on a node still attached to its
            // original parent.
            throw NotFoundError(call.Realm, "removeChild",
                "The node to be removed is not a child of this node.");
        }
        host.NotifyNodeIteratorPreRemoval(childEl);
        DomBridge.RemoveNthChild(element, idx);
        DomBridge.SetParent(childEl, null);
        host.InvalidateStyleScope(element);
        host.NotifyChildRemoved(element, childEl, idx, null, null);
        return call[0];
    }

    public static JsValue ReplaceChild(ITreeMutationHost host, DomElement element, in JsCall call)
    {
        if (call.Length < 2)
            return JsValue.Undefined;
        if (!call[0].IsObject || !call[1].IsObject)
            return JsValue.Undefined;
        var newEl = host.FindNode(call[0]);
        var oldEl = host.FindNode(call[1]);
        if (newEl == null || oldEl == null)
            return call[1];
        // Prevent circular references (HierarchyRequestError per DOM spec)
        if (ReferenceEquals(newEl, element) || element.IsDescendantOf(newEl))
            throw call.Realm.DomError("HierarchyRequestError", "The new child element contains the parent.");
        var idx = DomBridge.ChildIndexOf(element, oldEl);
        if (idx < 0)
        {
            // Same rule for replaceChild (DOM §4.2.3 replace: "If child's parent is not parent, then
            // throw a NotFoundError DOMException"), and the same misleading shape — it returned
            // call[1], which is what a successful replaceChild returns. This check is before any
            // mutation, which is where the specification puts the validation; the defensive re-check
            // further down runs after newEl has already been detached, so it keeps returning rather
            // than throwing out of a half-finished mutation.
            throw NotFoundError(call.Realm, "replaceChild",
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
                return call[1];
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
        return call[1]; // returns the old child
    }
}
