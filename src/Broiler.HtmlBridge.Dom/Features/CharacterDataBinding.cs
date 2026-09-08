using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The DOM <c>CharacterData</c> interface — <c>data</c> get/set, <c>length</c>, <c>splitText</c>
/// (Text), and the mutation methods <c>substringData</c>/<c>appendData</c>/<c>deleteData</c>/
/// <c>insertData</c>/<c>replaceData</c> — shared by Text and Comment nodes, co-located as an HtmlBridge
/// feature module (Phase 3, first slice off the 1599-line JsFunctionCallbacks/JsObjects.cs member file).
/// Read-side text access (<c>BridgeText</c>), node-type tests and the neutral tree helpers are the
/// bridge's <c>internal static</c> helpers; the notifying setter, text-node factory and wrapper factory
/// are reached through the narrow <see cref="ICharacterDataHost"/> contract. Previously the bridge's
/// <c>JsJsObjectsGetData045Core</c>..<c>ReplaceData053Core</c>.
/// </summary>
/// <remarks>
/// <para>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine type.
/// The two coercions are the realm's and not the handle's, which is the whole of what a page can
/// observe about them: an offset argument goes through <see cref="IJsValues.ToNumber"/> because
/// <c>t.deleteData("1", 2)</c> is legal and a string offset must parse, and a data argument goes
/// through <see cref="IJsValues.ToJsString"/> because an object assigned to <c>data</c> runs its own
/// <c>toString</c>.
/// </para>
/// </remarks>
internal static class CharacterDataBinding
{
    /// <summary>
    /// The <c>IndexSizeError</c> <c>DOMException</c> to <see langword="throw"/> when an offset is past
    /// the end of the data (DOM §4.10 — every one of these methods begins "If offset is greater than
    /// length, throw an IndexSizeError DOMException").
    /// </summary>
    /// <remarks>
    /// <para>
    /// These used to throw a plain error whose <em>message</em> was the string <c>"INDEX_SIZE_ERR"</c>
    /// — the legacy constant's name used as prose. Nothing a page can branch on came out of that:
    /// <c>e instanceof DOMException</c> was false, <c>e.name</c> was <c>"Error"</c> and <c>e.code</c>
    /// was <c>0</c>, so the two checks a caller actually writes both failed and the error read as an
    /// internal fault rather than the specified, recoverable one.
    /// </para>
    /// <para>
    /// It returns rather than throws so that each call site reads <c>throw IndexSizeError(…)</c>: that
    /// tells the compiler the path ends — the old helper threw from inside, so control appeared to
    /// continue past an out-of-range offset — and the reader that the throw is deliberate. The former
    /// second arm, a plain <c>JSException</c> for when no script context had been attached yet, is
    /// gone with the context: an operation reached through a call frame always has that frame's realm.
    /// </para>
    /// </remarks>
    private static Exception IndexSizeError(IJsRealm realm, string method, int offset, int length) =>
        realm.DomError(
            "IndexSizeError",
            $"Failed to execute '{method}' on 'CharacterData': The offset {offset} is greater than the node's length ({length}).");

    public static JsValue GetData(DomNode node, in JsCall call)
    {
        if (DomBridge.IsText(node) || DomBridge.IsComment(node))
            return JsValue.String(DomBridge.BridgeText(node));
        return JsValue.Undefined;
    }

    public static JsValue SetData(ICharacterDataHost host, DomNode node, in JsCall call)
    {
        if (DomBridge.IsText(node) || DomBridge.IsComment(node))
            host.SetCharacterData(node, call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty);
        return JsValue.Undefined;
    }

    public static JsValue GetLength(DomNode node, in JsCall call)
    {
        if (DomBridge.IsText(node) || DomBridge.IsComment(node))
            return JsValue.Number(DomBridge.BridgeText(node).Length);
        return JsValue.Number(node.ChildNodes.Count);
    }

    public static JsValue SplitText(ICharacterDataHost host, DomNode node, in JsCall call)
    {
        if (call.Length == 0)
        {
            // A plain Error, which is what the bare `new JSException(message)` this replaces produced.
            // A browser raises a TypeError for a missing required argument; that difference is
            // pre-existing and left alone here, because this pass is a change of vocabulary.
            throw call.Realm.Error(
                JsErrorKind.Error,
                "Failed to execute 'splitText' on 'Text': 1 argument required, but only 0 present.");
        }

        var offset = (int)call.Realm.ToNumber(call[0]);
        var text = DomBridge.BridgeText(node);
        // §4.11 splitText carries the same rule as the CharacterData methods: an offset past the end
        // is an IndexSizeError DOMException, not a bare error.
        if (offset < 0 || offset > text.Length)
            throw IndexSizeError(call.Realm, "splitText", offset, text.Length);
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

        // The split node keeps its wrapper. This used to drop it — "invalidate the cached JS wrapper so
        // length/data properties reflect the update" — from when a text node's members were own
        // properties of the wrapper. They are accessors on CharacterData.prototype now and read the
        // live node through the receiver, so there is nothing stale to invalidate, and dropping the
        // wrapper cost the node its script identity: DOM §4.11 splits a text node in place, so
        // `target.firstChild === t` holds after `t.splitText(n)` — measured in Chromium — where the
        // next wrapper minted for it was a different object.
        return host.WrapNode(newNode);
    }

    public static JsValue SubstringData(ICharacterDataHost host, DomNode node, in JsCall call)
    {
        var offset = call.Length > 0 ? (int)call.Realm.ToNumber(call[0]) : 0;
        var count = call.Length > 1 ? Math.Max(0, (int)call.Realm.ToNumber(call[1])) : 0;
        var text = DomBridge.BridgeText(node);
        if (offset < 0 || offset > text.Length)
            throw IndexSizeError(call.Realm, "substringData", offset, text.Length);
        var end = (int)Math.Min((long)offset + count, text.Length);
        return JsValue.String(text[offset..end]);
    }

    public static JsValue AppendData(ICharacterDataHost host, DomNode node, in JsCall call)
    {
        var data = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
        host.SetCharacterData(node, DomBridge.BridgeText(node) + data);
        return JsValue.Undefined;
    }

    public static JsValue DeleteData(ICharacterDataHost host, DomNode node, in JsCall call)
    {
        var offset = call.Length > 0 ? (int)call.Realm.ToNumber(call[0]) : 0;
        var count = call.Length > 1 ? Math.Max(0, (int)call.Realm.ToNumber(call[1])) : 0;
        var text = DomBridge.BridgeText(node);
        if (offset < 0 || offset > text.Length)
            throw IndexSizeError(call.Realm, "deleteData", offset, text.Length);
        var end = (int)Math.Min((long)offset + count, text.Length);
        host.SetCharacterData(node, text.Remove(offset, end - offset));
        return JsValue.Undefined;
    }

    public static JsValue InsertData(ICharacterDataHost host, DomNode node, in JsCall call)
    {
        var offset = call.Length > 0 ? (int)call.Realm.ToNumber(call[0]) : 0;
        var data = call.Length > 1 ? call.Realm.ToJsString(call[1]) : string.Empty;
        var text = DomBridge.BridgeText(node);
        if (offset < 0 || offset > text.Length)
            throw IndexSizeError(call.Realm, "insertData", offset, text.Length);
        host.SetCharacterData(node, text.Insert(offset, data));
        return JsValue.Undefined;
    }

    public static JsValue ReplaceData(ICharacterDataHost host, DomNode node, in JsCall call)
    {
        var offset = call.Length > 0 ? (int)call.Realm.ToNumber(call[0]) : 0;
        var count = call.Length > 1 ? Math.Max(0, (int)call.Realm.ToNumber(call[1])) : 0;
        var data = call.Length > 2 ? call.Realm.ToJsString(call[2]) : string.Empty;
        var text = DomBridge.BridgeText(node);
        if (offset < 0 || offset > text.Length)
            throw IndexSizeError(call.Realm, "replaceData", offset, text.Length);
        var end = (int)Math.Min((long)offset + count, text.Length);
        host.SetCharacterData(node, text.Remove(offset, end - offset).Insert(offset, data));
        return JsValue.Undefined;
    }
}
