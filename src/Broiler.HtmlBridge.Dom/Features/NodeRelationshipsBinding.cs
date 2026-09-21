using System;
using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Feature module for the DOM <c>Node</c> relationship operations shared by every node wrapper —
/// <c>contains</c>, <c>compareDocumentPosition</c>, <c>isSameNode</c>, <c>normalize</c>,
/// <c>isEqualNode</c>, <c>getRootNode</c> and <c>cloneNode</c>. The wrapper→node resolver,
/// character-data-aware <c>normalize()</c>, the root-node wrapper factory, the clone and
/// the plain JS-wrapper factory reach the bridge through <see cref="INodeRelationshipsHost"/>, while the
/// pure tree operations (<c>IsDescendantOf</c>/<c>IsEqualNode</c>/<c>CompareDocumentPosition</c>) and
/// the tree-root walk (<c>GetRootNode(composed)</c>) are called on <see cref="DomNode"/> directly.
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine type.
/// The <c>DOMException</c> a document clone raises comes from the call's own realm
/// (<see cref="IJsCalls.DomError"/>).
/// </remarks>
internal static class NodeRelationshipsBinding
{
    public static JsValue Contains(INodeRelationshipsHost host, DomNode node, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.False;
        if (!call[0].IsObject)
            return JsValue.False;
        var other = host.FindNode(call[0]);
        if (other == null)
            return JsValue.False;
        if (ReferenceEquals(node, other))
            return JsValue.True;
        return JsValue.Boolean(other.IsDescendantOf(node));
    }

    /// <summary>
    /// <c>compareDocumentPosition</c>: the canonical <see cref="DomNode.CompareDocumentPosition"/>
    /// bitmask, whose values are the <c>Node.DOCUMENT_POSITION_*</c> constants, passed through as a number.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the argument handling is the binding's own: a missing or non-object argument, or an object
    /// that is not a node wrapper, answers <c>0</c> rather than the <c>TypeError</c> Chromium throws.
    /// </para>
    /// <para>
    /// The canonical member finds each node's root by the same parent walk the bridge's tree root is
    /// (<c>GetRootNode</c>), so a shadow tree answers exactly what it did before: the bridge parents a
    /// <c>#shadow-root</c> element into its host, which makes a node in the shadow tree the host's
    /// descendant here, where Chromium treats the two as disconnected. Nodes with different roots — a
    /// created but unattached node, a node in a fragment — used to answer a bare
    /// <c>DOCUMENT_POSITION_DISCONNECTED</c> both ways; they now carry
    /// <c>IMPLEMENTATION_SPECIFIC</c> and a <c>PRECEDING</c>/<c>FOLLOWING</c> bit that reverses with
    /// the argument order and holds while both trees live, as DOM §4.4 requires.
    /// </para>
    /// </remarks>
    public static JsValue CompareDocumentPosition(INodeRelationshipsHost host, DomNode node, in JsCall call)
    {
        if (call.Length == 0 || !call[0].IsObject)
            return JsValue.Number(0);
        var other = host.FindNode(call[0]);
        if (other == null)
            return JsValue.Number(0);
        return JsValue.Number((int)node.CompareDocumentPosition(other));
    }

    public static JsValue IsSameNode(INodeRelationshipsHost host, DomNode node, in JsCall call)
    {
        if (call.Length == 0 || !call[0].IsObject)
            return JsValue.False;
        var other = host.FindNode(call[0]);
        return JsValue.Boolean(ReferenceEquals(node, other));
    }

    public static JsValue Normalize(INodeRelationshipsHost host, DomNode node, in JsCall call)
    {
        // normalize() on a character-data node is a no-op (it has no text children to merge).
        if (node is DomElement element)
            host.NormalizeNode(element);
        return JsValue.Undefined;
    }

    public static JsValue IsEqualNode(INodeRelationshipsHost host, DomNode node, in JsCall call)
    {
        if (call.Length == 0 || !call[0].IsObject)
            return JsValue.False;
        var other = host.FindNode(call[0]);
        // Delegate to the canonical Broiler.Dom.DomNode.IsEqualNode tree algorithm. The canonical
        // operation is null-tolerant (IsEqualNode(null) == false), so no explicit null check is
        // needed here.
        return JsValue.Boolean(node.IsEqualNode(other));
    }

    public static JsValue GetRootNode(INodeRelationshipsHost host, DomNode node, in JsCall call)
    {
        var composed = false;
        if (call.Length > 0 && call[0].IsObject)
        {
            // An absent `composed` reads as an absent value and is not truthy, which is the same
            // answer the engine-typed `!= null && !IsUndefined && !IsNull && .BooleanValue` chain
            // gave — every one of those arms is false under ECMAScript truthiness.
            composed = call.Realm.GetProperty(call[0], "composed").AsBoolean;
        }

        return host.WrapRootNode(node.GetRootNode(composed));
    }

    public static JsValue CloneNode(INodeRelationshipsHost host, DomNode node, in JsCall call)
    {
        var deep = call[0].AsBoolean;

        DomNode clone;
        try
        {
            clone = host.CloneDomElement(node, deep);
        }
        catch (InvalidOperationException)
        {
            // The canonical DOM kernel does not clone a document. A browser does — Chromium answers a
            // fresh node of type 9 — so this is a real gap and not a rule; what this turns it into is
            // a failure page script can read and branch on, rather than the kernel's own
            // InvalidOperationException arriving as the message of a plain Error.
            throw call.Realm.DomError(
                "NotSupportedError",
                "Failed to execute 'cloneNode' on 'Node': Cloning a Document is not supported.");
        }

        return host.WrapNode(clone);
    }
}
