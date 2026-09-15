using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    internal static JsValue StoreHistoryState(JsValue history, in JsCall call)
    {
        // `history.state` is re-defined rather than assigned, which is what the engine-typed
        // installer did: the slot keeps the attributes it was created with, and an argument the page
        // did not pass reads as null rather than undefined.
        call.Realm.DefineValue(history, "state", call.Length > 0 ? call[0] : JsValue.Null);

        return JsValue.Undefined;
    }
}
