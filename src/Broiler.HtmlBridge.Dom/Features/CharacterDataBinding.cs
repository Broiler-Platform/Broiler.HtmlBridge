using Broiler.Dom;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.BuiltIns.Number;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The DOM <c>CharacterData</c> interface — <c>data</c> get/set, <c>length</c>, <c>splitText</c>
/// (Text), and the mutation methods <c>substringData</c>/<c>appendData</c>/<c>deleteData</c>/
/// <c>insertData</c>/<c>replaceData</c> — shared by Text and Comment nodes, co-located as an HtmlBridge
/// feature module (Phase 3, first slice off the 1599-line JsFunctionCallbacks/JsObjects.cs member file).
/// Read-side text access (<c>BridgeText</c>), node-type tests and the neutral tree helpers are the
/// bridge's <c>internal static</c> helpers; the notifying setter, text-node factory, wrapper factory
/// and wrapper-cache invalidation are reached through the narrow <see cref="ICharacterDataHost"/>
/// contract. Previously the bridge's <c>JsJsObjectsGetData045Core</c>..<c>ReplaceData053Core</c>.
/// </summary>
internal static class CharacterDataBinding
{
    /// <summary>
    /// Throws the <c>IndexSizeError</c> <c>DOMException</c> that an offset past the end of the data
    /// must raise (DOM §4.10 — every one of these methods begins "If offset is greater than length,
    /// throw an IndexSizeError DOMException").
    /// </summary>
    /// <remarks>
    /// These used to throw a plain error whose <em>message</em> was the string <c>"INDEX_SIZE_ERR"</c>
    /// — the legacy constant's name used as prose. Nothing a page can branch on came out of that:
    /// <c>e instanceof DOMException</c> was false, <c>e.name</c> was <c>"Error"</c> and <c>e.code</c>
    /// was <c>0</c>, so the two checks a caller actually writes both failed and the error read as an
    /// internal fault rather than the specified, recoverable one. The bridge already had the
    /// machinery — <c>appendChild</c>'s HierarchyRequestError and <c>createElement</c>'s
    /// InvalidCharacterError go through this same helper and arrive correctly — so this is the
    /// CharacterData methods being wired to it, not a new mechanism.
    /// </remarks>
    private static void ThrowIndexSizeError(ICharacterDataHost host, string method, int offset, int length)
    {
        var message = $"Failed to execute '{method}' on 'CharacterData': The offset {offset} is greater than the node's length ({length}).";

        if (host.JsContext is { } context)
            DomBridge.ThrowDOMException(context, message, "IndexSizeError");

        // Only before the bridge is attached, when there is no realm to mint a DOMException in.
        throw new JSException(message);
    }

    public static JSValue GetData(DomNode node, in Arguments a)
    {
        if (DomBridge.IsText(node) || DomBridge.IsComment(node))
            return new JSString(DomBridge.BridgeText(node));
        return JSUndefined.Value;
    }

    public static JSValue SetData(ICharacterDataHost host, DomNode node, in Arguments a)
    {
        if (DomBridge.IsText(node) || DomBridge.IsComment(node))
            host.SetCharacterData(node, a.Length > 0 ? a[0].ToString() : string.Empty);
        return JSUndefined.Value;
    }

    public static JSValue GetLength(DomNode node, in Arguments a)
    {
        if (DomBridge.IsText(node) || DomBridge.IsComment(node))
            return new JSNumber(DomBridge.BridgeText(node).Length);
        return new JSNumber(node.ChildNodes.Count);
    }

    public static JSValue SplitText(ICharacterDataHost host, DomNode node, in Arguments a)
    {
        if (a.Length == 0)
            throw new JSException("Failed to execute 'splitText' on 'Text': 1 argument required, but only 0 present.");
        var offset = (int)a[0].DoubleValue;
        var text = DomBridge.BridgeText(node);
        // §4.11 splitText carries the same rule as the CharacterData methods: an offset past the end
        // is an IndexSizeError DOMException, not a bare error.
        if (offset < 0 || offset > text.Length)
            ThrowIndexSizeError(host, "splitText", offset, text.Length);
        var remainingText = text[offset..];
        DomBridge.SetBridgeText(node, text[..offset]);
        var newNode = host.CreateBridgeTextNode(remainingText);
        // Insert new node as next sibling.
        if (DomBridge.ParentEl(node) != null)
        {
            var idx = DomBridge.ChildIndexOf(DomBridge.ParentEl(node), node);
            // Single canonical insert of the fresh split node as next sibling (the prior SetParent
            // appended it at the end first, then InsertChildAt re-moved it — spurious records).
            DomBridge.InsertChildAt(DomBridge.ParentEl(node), idx + 1, newNode);
        }

        // The split node keeps its wrapper. This used to drop it — "invalidate the cached JSObject so
        // length/data properties reflect the update" — from when a text node's members were own
        // properties of the wrapper. They are accessors on CharacterData.prototype now and read the
        // live node through the receiver, so there is nothing stale to invalidate, and dropping the
        // wrapper cost the node its script identity: DOM §4.11 splits a text node in place, so
        // `target.firstChild === t` holds after `t.splitText(n)` — measured in Chromium — where the
        // next wrapper minted for it was a different object.
        return host.ToJSObject(newNode);
    }

    public static JSValue SubstringData(ICharacterDataHost host, DomNode node, in Arguments a)
    {
        var offset = a.Length > 0 ? (int)a[0].DoubleValue : 0;
        var count = a.Length > 1 ? Math.Max(0, (int)a[1].DoubleValue) : 0;
        var text = DomBridge.BridgeText(node);
        if (offset < 0 || offset > text.Length)
            ThrowIndexSizeError(host, "substringData", offset, text.Length);
        var end = (int)Math.Min((long)offset + count, text.Length);
        return new JSString(text[offset..end]);
    }

    public static JSValue AppendData(ICharacterDataHost host, DomNode node, in Arguments a)
    {
        var data = a.Length > 0 ? a[0].ToString() : string.Empty;
        host.SetCharacterData(node, DomBridge.BridgeText(node) + data);
        return JSUndefined.Value;
    }

    public static JSValue DeleteData(ICharacterDataHost host, DomNode node, in Arguments a)
    {
        var offset = a.Length > 0 ? (int)a[0].DoubleValue : 0;
        var count = a.Length > 1 ? Math.Max(0, (int)a[1].DoubleValue) : 0;
        var text = DomBridge.BridgeText(node);
        if (offset < 0 || offset > text.Length)
            ThrowIndexSizeError(host, "deleteData", offset, text.Length);
        var end = (int)Math.Min((long)offset + count, text.Length);
        host.SetCharacterData(node, text.Remove(offset, end - offset));
        return JSUndefined.Value;
    }

    public static JSValue InsertData(ICharacterDataHost host, DomNode node, in Arguments a)
    {
        var offset = a.Length > 0 ? (int)a[0].DoubleValue : 0;
        var data = a.Length > 1 ? a[1].ToString() : string.Empty;
        var text = DomBridge.BridgeText(node);
        if (offset < 0 || offset > text.Length)
            ThrowIndexSizeError(host, "insertData", offset, text.Length);
        host.SetCharacterData(node, text.Insert(offset, data));
        return JSUndefined.Value;
    }

    public static JSValue ReplaceData(ICharacterDataHost host, DomNode node, in Arguments a)
    {
        var offset = a.Length > 0 ? (int)a[0].DoubleValue : 0;
        var count = a.Length > 1 ? Math.Max(0, (int)a[1].DoubleValue) : 0;
        var data = a.Length > 2 ? a[2].ToString() : string.Empty;
        var text = DomBridge.BridgeText(node);
        if (offset < 0 || offset > text.Length)
            ThrowIndexSizeError(host, "replaceData", offset, text.Length);
        var end = (int)Math.Min((long)offset + count, text.Length);
        host.SetCharacterData(node, text.Remove(offset, end - offset).Insert(offset, data));
        return JSUndefined.Value;
    }
}
