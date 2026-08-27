using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{

    private JSValue JsCallbackStopPropagation001Core(ref bool legacyCancelBubble, in Arguments _)
    {
        legacyCancelBubble = true;
        return JSUndefined.Value;
    }


    private JSValue JsCallbackStopImmediatePropagation002Core(ref bool immediateStopped, ref bool legacyCancelBubble, in Arguments _)
    {
        immediateStopped = true;
        legacyCancelBubble = true;
        return JSUndefined.Value;
    }


    private JSValue JsCallbackPreventDefault003Core(bool currentListenerPassive, JSObject evt, ref bool prevented, in Arguments _)
    {
        var cancelable = evt[(KeyString)"cancelable"];
        if (!currentListenerPassive && cancelable != null && cancelable.BooleanValue)
        {
            prevented = true;
            evt[(KeyString)"defaultPrevented"] = JSBoolean.True;
        }

        return JSUndefined.Value;
    }


    private JSValue JsCallbackSetCancelBubble005Core(ref bool legacyCancelBubble, in Arguments setArgs)
    {
        if (setArgs.Length > 0 && setArgs[0].BooleanValue)
        {
            legacyCancelBubble = true;
        }

        return JSUndefined.Value;
    }


    private JSValue JsCallbackSetReturnValue007Core(bool currentListenerPassive, JSObject evt, ref bool prevented, in Arguments setArgs)
    {
        var cancelable = evt[(KeyString)"cancelable"];
        if (setArgs.Length > 0 && !setArgs[0].BooleanValue && !currentListenerPassive && cancelable != null && cancelable.BooleanValue)
        {
            prevented = true;
            evt[(KeyString)"defaultPrevented"] = JSBoolean.True;
        }

        return JSUndefined.Value;
    }

}
