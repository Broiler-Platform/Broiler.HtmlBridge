using System;
using Broiler.Dom;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Number;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Phase 3 feature module for the DOM <c>Node</c> relationship operations shared by every node wrapper —
/// <c>contains</c>, <c>compareDocumentPosition</c>, <c>isSameNode</c>, <c>normalize</c>,
/// <c>isEqualNode</c>, <c>getRootNode</c> and <c>cloneNode</c>. These were the bridge's
/// <c>JsJsObjectsContains073Core</c>..<c>CloneNode079Core</c> callbacks; the JS-object→node resolver, the
/// tree-root walk, character-data-aware <c>normalize()</c>, the root-node wrapper factory, the clone and
/// the plain JS-wrapper factory reach the bridge through <see cref="INodeRelationshipsHost"/>, while the
/// pure tree predicates (<c>IsDescendantOf</c>/<c>IsEqualNode</c> on <see cref="DomNode"/>), document-order
/// comparison (<c>CompareTreeOrder</c>) and the shadow-root walk (<c>FindContainingShadowRoot</c>) are
/// called directly (the latter two are the bridge's <c>internal static</c> helpers).
/// </summary>
internal static class NodeRelationshipsBinding
{
    public static JSValue Contains(INodeRelationshipsHost host, DomNode node, in Arguments a)
    {
        if (a.Length == 0)
            return JSBoolean.False;
        if (a[0] is not JSObject otherObj)
            return JSBoolean.False;
        var other = host.FindDomNodeByJSObject(otherObj);
        if (other == null)
            return JSBoolean.False;
        if (ReferenceEquals(node, other))
            return JSBoolean.True;
        return other.IsDescendantOf(node) ? JSBoolean.True : JSBoolean.False;
    }

    public static JSValue CompareDocumentPosition(INodeRelationshipsHost host, DomNode node, in Arguments a)
    {
        const int documentPositionDisconnected = 0x01;
        const int documentPositionPreceding = 0x02;
        const int documentPositionFollowing = 0x04;
        const int documentPositionContains = 0x08;
        const int documentPositionContainedBy = 0x10;
        if (a.Length == 0 || a[0] is not JSObject otherObj)
            return new JSNumber(0);
        var other = host.FindDomNodeByJSObject(otherObj);
        if (other == null || ReferenceEquals(node, other))
            return new JSNumber(0);
        if (!ReferenceEquals(host.GetTreeRoot(node), host.GetTreeRoot(other)))
            return new JSNumber(documentPositionDisconnected);
        if (other.IsDescendantOf(node))
            return new JSNumber(documentPositionFollowing | documentPositionContainedBy);
        if (node.IsDescendantOf(other))
            return new JSNumber(documentPositionPreceding | documentPositionContains);
        return new JSNumber(DomBridge.CompareTreeOrder(node, other) < 0 ? documentPositionFollowing : documentPositionPreceding);
    }

    public static JSValue IsSameNode(INodeRelationshipsHost host, DomNode node, in Arguments a)
    {
        if (a.Length == 0 || a[0] is not JSObject otherObj)
            return JSBoolean.False;
        var other = host.FindDomNodeByJSObject(otherObj);
        return ReferenceEquals(node, other) ? JSBoolean.True : JSBoolean.False;
    }

    public static JSValue Normalize(INodeRelationshipsHost host, DomNode node, in Arguments _)
    {
        // normalize() on a character-data node is a no-op (it has no text children to merge).
        if (node is DomElement element)
            host.NormalizeNode(element);
        return JSUndefined.Value;
    }

    public static JSValue IsEqualNode(INodeRelationshipsHost host, DomNode node, in Arguments a)
    {
        if (a.Length == 0 || a[0] is not JSObject otherObj)
            return JSBoolean.False;
        var other = host.FindDomNodeByJSObject(otherObj);
        // Phase 4 items 4/5: delegate to the canonical Broiler.Dom.DomNode.IsEqualNode tree
        // algorithm (promoted via patches/0001, now applied and pinned in the submodule). The
        // canonical operation is null-tolerant (IsEqualNode(null) == false), so the former explicit
        // null check is subsumed. The old bridge copy (NodesAreEqual/CanonicalAttributesAreEqual) is
        // deleted; behaviour is pinned by IsEqualNodePromotionTests.
        return node.IsEqualNode(other) ? JSBoolean.True : JSBoolean.False;
    }

    public static JSValue GetRootNode(INodeRelationshipsHost host, DomNode node, in Arguments a)
    {
        var composed = false;
        if (a.Length > 0 && a[0] is JSObject options)
        {
            var composedValue = options[(KeyString)"composed"];
            composed = composedValue != null && !composedValue.IsUndefined && !composedValue.IsNull && composedValue.BooleanValue;
        }

        if (!composed)
        {
            var shadowRoot = DomBridge.FindContainingShadowRoot(node);
            if (shadowRoot != null)
                return host.ToJSObject(shadowRoot);
        }

        return host.ToJSRootNode(host.GetTreeRoot(node));
    }

    public static JSValue CloneNode(INodeRelationshipsHost host, DomNode node, in Arguments a)
    {
        var deep = a.Length > 0 && a[0].BooleanValue;

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
            DomBridge.ThrowDOMException(
                host.JsContext,
                "Failed to execute 'cloneNode' on 'Node': Cloning a Document is not supported.",
                "NotSupportedError");
            return JSUndefined.Value;
        }

        return host.ToJSObject(clone);
    }
}
