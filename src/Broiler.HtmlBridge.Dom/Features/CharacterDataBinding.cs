using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The DOM <c>CharacterData</c> interface — <c>data</c> get/set, <c>length</c>, <c>splitText</c>
/// (Text), and the mutation methods <c>substringData</c>/<c>appendData</c>/<c>deleteData</c>/
/// <c>insertData</c>/<c>replaceData</c> — shared by Text and Comment nodes, co-located as an HtmlBridge
/// feature module.
/// Read-side text access (<c>BridgeText</c>), node-type tests and the neutral tree helpers are the
/// bridge's <c>internal static</c> helpers; the notifying setter, text-node factory and wrapper factory
/// are reached through the narrow <see cref="ICharacterDataHost"/> contract.
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
    /// A plain error whose <em>message</em> is the string <c>"INDEX_SIZE_ERR"</c> — the legacy
    /// constant's name used as prose — gives a page nothing to branch on:
    /// <c>e instanceof DOMException</c> is false, <c>e.name</c> is <c>"Error"</c> and <c>e.code</c>
    /// is <c>0</c>, so the two checks a caller actually writes both fail and the error reads as an
    /// internal fault rather than the specified, recoverable one.
    /// </para>
    /// <para>
    /// It returns rather than throws so that each call site reads <c>throw IndexSizeError(…)</c>: that
    /// tells the compiler the path ends — a helper that threw from inside would leave control
    /// appearing to continue past an out-of-range offset — and tells the reader that the throw is
    /// deliberate. It needs no realm fallback: an operation reached through a call frame always has
    /// that frame's realm.
    /// </para>
    /// </remarks>
    private static Exception IndexSizeError(IJsRealm realm, string method, int offset, int length) =>
        realm.DomError(
            "IndexSizeError",
            $"Failed to execute '{method}' on 'CharacterData': The offset {offset} is greater than the node's length ({length}).");

    public static JsValue GetData(DomNode node, in JsCall call)
    {
        if (DomBridgeUtils.IsText(node) || DomBridgeUtils.IsComment(node))
            return JsValue.String(DomBridgeUtils.BridgeText(node));
        return JsValue.Undefined;
    }

    public static JsValue SetData(ICharacterDataHost host, DomNode node, in JsCall call)
    {
        if (DomBridgeUtils.IsText(node) || DomBridgeUtils.IsComment(node))
            host.SetCharacterData(node, call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty);
        return JsValue.Undefined;
    }

    public static JsValue GetLength(DomNode node, in JsCall call)
    {
        if (node is DomCharacterData characterData)
            return JsValue.Number(characterData.Length);
        if (DomBridgeUtils.IsText(node) || DomBridgeUtils.IsComment(node))
            return JsValue.Number(DomBridgeUtils.BridgeText(node).Length);
        return JsValue.Number(node.ChildNodes.Count);
    }

    public static JsValue SplitText(ICharacterDataHost host, DomNode node, in JsCall call)
    {
        if (call.Length == 0)
        {
            // A plain Error. A browser raises a TypeError for a missing required argument; that
            // difference is long-standing and deliberately left alone here.
            throw call.Realm.Error(
                JsErrorKind.Error,
                "Failed to execute 'splitText' on 'Text': 1 argument required, but only 0 present.");
        }

        var offset = (int)call.Realm.ToNumber(call[0]);
        if (node is DomText domText)
        {
            try
            {
                var newNode = domText.SplitText(offset);
                return host.WrapNode(newNode);
            }
            catch (DomException ex) when (ex.Name == "IndexSizeError")
            {
                throw IndexSizeError(call.Realm, "splitText", offset, domText.Length);
            }
        }

        var text = DomBridgeUtils.BridgeText(node);
        // §4.11 splitText carries the same rule as the CharacterData methods: an offset past the end
        // is an IndexSizeError DOMException, not a bare error.
        if (offset < 0 || offset > text.Length)
            throw IndexSizeError(call.Realm, "splitText", offset, text.Length);
        var remainingText = text[offset..];
        DomBridgeUtils.SetBridgeText(node, text[..offset]);
        var fallbackNode = host.CreateBridgeTextNode(remainingText);
        // Insert new node as next sibling.
        if (DomBridgeUtils.ParentEl(node) != null)
        {
            var idx = DomBridgeUtils.ChildIndexOf(DomBridgeUtils.ParentEl(node), node);
            DomBridgeUtils.InsertChildAt(DomBridgeUtils.ParentEl(node), idx + 1, fallbackNode);
        }

        return host.WrapNode(fallbackNode);
    }

    public static JsValue SubstringData(ICharacterDataHost host, DomNode node, in JsCall call)
    {
        var offset = call.Length > 0 ? (int)call.Realm.ToNumber(call[0]) : 0;
        var count = call.Length > 1 ? Math.Max(0, (int)call.Realm.ToNumber(call[1])) : 0;
        if (node is DomCharacterData characterData)
        {
            try
            {
                return JsValue.String(characterData.SubstringData(offset, count));
            }
            catch (DomException ex) when (ex.Name == "IndexSizeError")
            {
                throw IndexSizeError(call.Realm, "substringData", offset, characterData.Length);
            }
        }

        var text = DomBridgeUtils.BridgeText(node);
        if (offset < 0 || offset > text.Length)
            throw IndexSizeError(call.Realm, "substringData", offset, text.Length);
        var end = (int)Math.Min((long)offset + count, text.Length);
        return JsValue.String(text[offset..end]);
    }

    public static JsValue AppendData(ICharacterDataHost host, DomNode node, in JsCall call)
    {
        var data = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
        if (node is DomCharacterData characterData)
        {
            characterData.AppendData(data);
            return JsValue.Undefined;
        }

        host.SetCharacterData(node, DomBridgeUtils.BridgeText(node) + data);
        return JsValue.Undefined;
    }

    public static JsValue DeleteData(ICharacterDataHost host, DomNode node, in JsCall call)
    {
        var offset = call.Length > 0 ? (int)call.Realm.ToNumber(call[0]) : 0;
        var count = call.Length > 1 ? Math.Max(0, (int)call.Realm.ToNumber(call[1])) : 0;
        if (node is DomCharacterData characterData)
        {
            try
            {
                characterData.DeleteData(offset, count);
                return JsValue.Undefined;
            }
            catch (DomException ex) when (ex.Name == "IndexSizeError")
            {
                throw IndexSizeError(call.Realm, "deleteData", offset, characterData.Length);
            }
        }

        var text = DomBridgeUtils.BridgeText(node);
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
        if (node is DomCharacterData characterData)
        {
            try
            {
                characterData.InsertData(offset, data);
                return JsValue.Undefined;
            }
            catch (DomException ex) when (ex.Name == "IndexSizeError")
            {
                throw IndexSizeError(call.Realm, "insertData", offset, characterData.Length);
            }
        }

        var text = DomBridgeUtils.BridgeText(node);
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
        if (node is DomCharacterData characterData)
        {
            try
            {
                characterData.ReplaceData(offset, count, data);
                return JsValue.Undefined;
            }
            catch (DomException ex) when (ex.Name == "IndexSizeError")
            {
                throw IndexSizeError(call.Realm, "replaceData", offset, characterData.Length);
            }
        }

        var text = DomBridgeUtils.BridgeText(node);
        if (offset < 0 || offset > text.Length)
            throw IndexSizeError(call.Realm, "replaceData", offset, text.Length);
        var end = (int)Math.Min((long)offset + count, text.Length);
        host.SetCharacterData(node, text.Remove(offset, end - offset).Insert(offset, data));
        return JsValue.Undefined;
    }
}
