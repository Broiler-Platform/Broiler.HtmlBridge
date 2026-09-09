using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>document</c>-node mutation methods — <c>document.childNodes</c> (getter),
/// <c>document.removeChild</c>, <c>document.appendChild</c>, <c>document.insertBefore</c> — co-located
/// as an HtmlBridge feature module (Phase 3). Each resolves its argument node, performs the
/// structural move on the document node via the bridge's neutral <c>internal static</c> tree helpers,
/// and fires the mutation-observer / node-iterator notifications. The document node, wrapper factory,
/// reverse lookup and notifications are reached through the <see cref="INodeMutationHost"/> contract.
/// Previously the bridge's <c>JsRegistrationGetChildNodes046Core</c>..<c>InsertBefore049Core</c> in the
/// shared JsFunctionCallbacks/Registration.cs grab-bag.
/// </summary>
/// <remarks>
/// The call frame is JSEAL's: <c>DomBridge/Registration/Document.cs</c> mints all seven members
/// through the realm. The DOM exceptions go through <c>JsCall.Realm</c>'s own <c>DomError</c>, which
/// constructs the same object against the same <c>DOMException</c> global the bridge's
/// <c>ThrowDOMException</c> reached — including its "no constructor yet" fallback, so the
/// before-attach case the old <c>JsContext</c> null check covered is still covered.
/// </remarks>
internal static class NodeMutationBinding
{
    /// <summary>
    /// The <c>NotFoundError</c> <c>DOMException</c> the pre-insert and pre-remove steps require
    /// when the named child is not a child of this parent (DOM §4.2.3) — the document-level
    /// counterpart of <c>TreeMutationBinding</c>'s helper, which carries the same rule for elements.
    /// </summary>
    private static Exception NotFoundError(IJsRealm realm, string method, string detail) =>
        realm.DomError("NotFoundError", $"Failed to execute '{method}' on 'Node': {detail}");

    /// <summary>
    /// <c>document.childNodes</c> — a live <c>NodeList</c> of the document node's children, which for
    /// a parsed HTML document is the doctype and then <c>&lt;html&gt;</c>.
    /// </summary>
    /// <remarks>
    /// This filtered to elements, so the doctype was absent from it and
    /// <c>document.childNodes.length</c> answered 1 where a browser answers 2 — and
    /// <c>document.firstChild</c>, which does not filter, returned a node
    /// <c>document.childNodes[0]</c> disagreed with. The list is also live and a real
    /// <c>NodeList</c> now, for the reasons in <see cref="DomCollectionBinding"/>; it used to be a
    /// snapshot array.
    /// </remarks>
    public static JsValue GetChildNodes(INodeMutationHost host, in JsCall call) =>
        DomCollectionBinding.NodeList(call.Realm, () =>
        {
            var nodes = new List<JsValue>();
            foreach (var child in host.DocumentNode.ChildNodes)
                nodes.Add(host.WrapNode(child));
            return nodes;
        });

    public static JsValue RemoveChild(INodeMutationHost host, in JsCall call)
    {
        if (!call[0].IsObject)
            return JsValue.Null;
        var childEl = host.FindDomNode(call[0]);
        if (childEl != null)
        {
            var doc = host.DocumentNode;
            var idx = DomBridge.ChildIndexOf(doc, childEl);
            if (idx < 0)
            {
                // DOM §4.2.3 pre-remove: "If child's parent is not parent, then throw a NotFoundError
                // DOMException." This fell straight through to `return a[0]` — and a[0] is what a
                // SUCCESSFUL removeChild returns, so the caller was handed the node back as if it had
                // been detached while the tree was untouched.
                throw NotFoundError(call.Realm, "removeChild",
                    "The node to be removed is not a child of this node.");
            }

            host.NotifyNodeIteratorPreRemoval(childEl);
            DomBridge.RemoveNthChild(doc, idx);
            DomBridge.SetParent(childEl, null);
            host.NotifyChildRemoved(doc, childEl, idx);
        }

        return call[0];
    }

    public static JsValue AppendChild(INodeMutationHost host, in JsCall call)
    {
        if (!call[0].IsObject)
            return JsValue.Null;
        var childEl = host.FindDomNode(call[0]);
        if (childEl != null)
        {
            if (DomBridge.ParentEl(childEl) != null)
            {
                var oldParent = DomBridge.ParentEl(childEl);
                var oldIndex = DomBridge.ChildIndexOf(oldParent, childEl);
                if (oldIndex >= 0)
                {
                    host.NotifyNodeIteratorPreRemoval(childEl);
                    DomBridge.RemoveNthChild(oldParent, oldIndex);
                    host.NotifyChildRemoved(oldParent, childEl, oldIndex);
                }
            }

            var doc = host.DocumentNode;
            // Single canonical append (the move-block above already detached childEl). The prior
            // SetParent(childEl, doc) did the append, leaving this AppendChild a redundant no-op.
            doc.AppendChild(childEl);
            host.NotifyChildAdded(doc, childEl, doc.ChildNodes.Count - 1);
        }

        return call[0];
    }

    /// <summary>
    /// DOM §4.2.6 <c>ParentNode.append()</c> on the document node: insert each argument after
    /// the document's last child.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Document</c> includes the <c>ParentNode</c> mixin, so <c>append</c>, <c>prepend</c>
    /// and <c>replaceChildren</c> exist on it exactly as they do on an element. The bridge
    /// bound only the <c>Node</c>-level <c>appendChild</c>/<c>insertBefore</c>/<c>removeChild</c>,
    /// so <c>document.append(x)</c> was <c>undefined</c> — and a script calling it threw
    /// mid-way, after whatever it had already done to the tree. That is worse than it sounds
    /// for a reftest: WPT's <c>quirks/tables-inherit-color-from-body-quirk-007</c> does
    /// <c>documentElement.remove()</c> and *then* <c>document.append(clone)</c>, so the throw
    /// left a document with no root element at all and the test rendered blank.
    /// </para>
    /// <para>
    /// The insert mirrors <see cref="AppendChild"/>: a node that already has a parent is
    /// detached first (with the observer/iterator notifications that move owes), so
    /// <c>append</c> moves rather than duplicating.
    /// </para>
    /// </remarks>
    public static JsValue Append(INodeMutationHost host, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Undefined;

        var doc = host.DocumentNode;
        foreach (var node in host.BuildChildNodeArgumentNodes(call.Arguments))
            InsertIntoDocumentAt(host, call.Realm, node, doc.ChildNodes.Count);

        return JsValue.Undefined;
    }

    /// <summary>
    /// DOM §4.2.6 <c>ParentNode.prepend()</c> on the document node: insert the arguments before
    /// the document's first child, keeping their order relative to each other.
    /// </summary>
    public static JsValue Prepend(INodeMutationHost host, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Undefined;

        var insertIndex = 0;
        foreach (var node in host.BuildChildNodeArgumentNodes(call.Arguments))
            InsertIntoDocumentAt(host, call.Realm, node, insertIndex++);

        return JsValue.Undefined;
    }

    /// <summary>
    /// DOM §4.2.6 <c>ParentNode.replaceChildren()</c> on the document node: remove every
    /// existing child, then insert the arguments. Called with none, it empties the document.
    /// </summary>
    public static JsValue ReplaceChildren(INodeMutationHost host, in JsCall call)
    {
        var doc = host.DocumentNode;

        // The nodes are resolved before anything is removed: an argument may be a node that
        // is currently a child of the document, and clearing first would detach it and then
        // re-insert it, which is the same end state but a different set of mutation records.
        var nodes = call.Length == 0 ? [] : host.BuildChildNodeArgumentNodes(call.Arguments);

        for (var index = doc.ChildNodes.Count - 1; index >= 0; index--)
        {
            var child = DomBridge.ChildAt(doc, index);
            host.NotifyNodeIteratorPreRemoval(child);
            DomBridge.RemoveNthChild(doc, index);
            DomBridge.SetParent(child, null);
            host.NotifyChildRemoved(doc, child, index);
        }

        var insertIndex = 0;
        foreach (var node in nodes)
            InsertIntoDocumentAt(host, call.Realm, node, insertIndex++);

        return JsValue.Undefined;
    }

    /// <summary>
    /// Detaches <paramref name="node"/> from its current parent, if any, and inserts it into the
    /// document at <paramref name="index"/>, firing the notifications each half owes.
    /// </summary>
    private static void InsertIntoDocumentAt(INodeMutationHost host, IJsRealm realm, DomNode node, int index)
    {
        RejectElementBeforeDoctype(host, realm, node, index);

        var oldParent = DomBridge.ParentEl(node);
        if (oldParent != null)
        {
            var oldIndex = DomBridge.ChildIndexOf(oldParent, node);
            if (oldIndex >= 0)
            {
                host.NotifyNodeIteratorPreRemoval(node);
                DomBridge.RemoveNthChild(oldParent, oldIndex);
                host.NotifyChildRemoved(oldParent, node, oldIndex);
            }
        }

        var doc = host.DocumentNode;
        var at = Math.Clamp(index, 0, doc.ChildNodes.Count);
        if (at == doc.ChildNodes.Count)
            doc.AppendChild(node);
        else
            DomBridge.InsertChildAt(doc, at, node);

        host.NotifyChildAdded(doc, node, at);
    }

    /// <summary>
    /// DOM §4.2.3 "ensure pre-insertion validity", the one clause of it a `prepend` on a document
    /// can trip: an element may not be inserted before the document's doctype. Without the check
    /// the tree accepts `&lt;html&gt;` ahead of `&lt;!DOCTYPE&gt;`, which no serializer or
    /// documentElement lookup expects — the document reads as having no root at all, silently.
    /// <para>
    /// Scoped to the three ParentNode methods this file added. <see cref="AppendChild"/> and
    /// <see cref="InsertBefore"/> perform no validity checks either, and giving them one is a
    /// behaviour change to existing bindings rather than part of adding the mixin.
    /// </para>
    /// </summary>
    private static void RejectElementBeforeDoctype(INodeMutationHost host, IJsRealm realm, DomNode node, int index)
    {
        if (node.NodeType != DomNodeType.Element)
            return;

        var doc = host.DocumentNode;
        for (var i = index; i < doc.ChildNodes.Count; i++)
        {
            if (doc.ChildNodes[i].NodeType != DomNodeType.DocumentType)
                continue;

            throw realm.DomError("HierarchyRequestError", "Cannot insert an element before the doctype.");
        }
    }

    public static JsValue InsertBefore(INodeMutationHost host, in JsCall call)
    {
        if (!call[0].IsObject)
            return JsValue.Null;
        var newEl = host.FindDomNode(call[0]);
        if (newEl == null)
            return call[0];
        if (DomBridge.ParentEl(newEl) != null)
        {
            var oldParent = DomBridge.ParentEl(newEl);
            var oldIndex = DomBridge.ChildIndexOf(oldParent, newEl);
            if (oldIndex >= 0)
            {
                host.NotifyNodeIteratorPreRemoval(newEl);
                DomBridge.RemoveNthChild(oldParent, oldIndex);
                host.NotifyChildRemoved(oldParent, newEl, oldIndex);
            }
        }

        var doc = host.DocumentNode;
        if (call[1].IsObject)
        {
            // A reference node WAS supplied, so it must be a child of this parent: DOM §4.2.3
            // pre-insert says "If child is non-null and its parent is not parent, then throw a
            // NotFoundError DOMException." This used to fall through to the append below, which is
            // the worst outcome of the three shapes this family had — not a silent no-op but a
            // silent mutation into a position the caller never asked for, leaving the node at the
            // end of the document instead of before the reference.
            var refEl = host.FindDomNode(call[1]);
            var idx = refEl != null ? DomBridge.ChildIndexOf(doc, refEl) : -1;
            if (idx < 0)
            {
                throw NotFoundError(call.Realm, "insertBefore",
                    "The node before which the new node is to be inserted is not a child of this node.");
            }

            // Single canonical insert (newEl detached above); the prior SetParent-append +
            // reposition fired spurious add-at-end/remove records.
            DomBridge.InsertChildAt(doc, idx, newEl);
            host.NotifyChildAdded(doc, newEl, idx);
            return call[0];
        }

        // A null or absent refChild means append — that IS the specified behaviour, not a fallback.
        doc.AppendChild(newEl);
        host.NotifyChildAdded(doc, newEl, doc.ChildNodes.Count - 1);
        return call[0];
    }
}
