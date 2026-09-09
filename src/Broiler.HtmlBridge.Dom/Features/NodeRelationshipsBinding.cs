using System;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Phase 3 feature module for the DOM <c>Node</c> relationship operations shared by every node wrapper —
/// <c>contains</c>, <c>compareDocumentPosition</c>, <c>isSameNode</c>, <c>normalize</c>,
/// <c>isEqualNode</c>, <c>getRootNode</c> and <c>cloneNode</c>. These were the bridge's
/// <c>JsJsObjectsContains073Core</c>..<c>CloneNode079Core</c> callbacks; the wrapper→node resolver, the
/// tree-root walk, character-data-aware <c>normalize()</c>, the root-node wrapper factory, the clone and
/// the plain JS-wrapper factory reach the bridge through <see cref="INodeRelationshipsHost"/>, while the
/// pure tree predicates (<c>IsDescendantOf</c>/<c>IsEqualNode</c> on <see cref="DomNode"/>), document-order
/// comparison (<c>CompareTreeOrder</c>) and the shadow-root walk (<c>FindContainingShadowRoot</c>) are
/// called directly (the latter two are the bridge's <c>internal static</c> helpers).
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine type.
/// The <c>DOMException</c> a document clone raises comes from the call's own realm
/// (<see cref="IJsCalls.DomError"/>) rather than from a script context the host used to hand over for
/// that single purpose.
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

    public static JsValue CompareDocumentPosition(INodeRelationshipsHost host, DomNode node, in JsCall call)
    {
        const int documentPositionDisconnected = 0x01;
        const int documentPositionPreceding = 0x02;
        const int documentPositionFollowing = 0x04;
        const int documentPositionContains = 0x08;
        const int documentPositionContainedBy = 0x10;
        if (call.Length == 0 || !call[0].IsObject)
            return JsValue.Number(0);
        var other = host.FindNode(call[0]);
        if (other == null || ReferenceEquals(node, other))
            return JsValue.Number(0);
        if (!ReferenceEquals(host.GetTreeRoot(node), host.GetTreeRoot(other)))
            return JsValue.Number(documentPositionDisconnected);
        if (other.IsDescendantOf(node))
            return JsValue.Number(documentPositionFollowing | documentPositionContainedBy);
        if (node.IsDescendantOf(other))
            return JsValue.Number(documentPositionPreceding | documentPositionContains);
        return JsValue.Number(DomBridge.CompareTreeOrder(node, other) < 0 ? documentPositionFollowing : documentPositionPreceding);
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
        // Phase 4 items 4/5: delegate to the canonical Broiler.Dom.DomNode.IsEqualNode tree
        // algorithm (promoted via patches/0001, now applied and pinned in the submodule). The
        // canonical operation is null-tolerant (IsEqualNode(null) == false), so the former explicit
        // null check is subsumed. The old bridge copy (NodesAreEqual/CanonicalAttributesAreEqual) is
        // deleted; behaviour is pinned by IsEqualNodePromotionTests.
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

        if (!composed)
        {
            var shadowRoot = DomBridge.FindContainingShadowRoot(node);
            if (shadowRoot != null)
                return host.WrapNode(shadowRoot);
        }

        return host.WrapRootNode(host.GetTreeRoot(node));
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
            // a failure page script can read. It used to surface the kernel's own
            // InvalidOperationException, so an internal phrase ("the Phase 1 kernel") reached the
            // page as the message of a plain Error, and nothing could branch on it.
            throw call.Realm.DomError(
                "NotSupportedError",
                "Failed to execute 'cloneNode' on 'Node': Cloning a Document is not supported.");
        }

        return host.WrapNode(clone);
    }
}
