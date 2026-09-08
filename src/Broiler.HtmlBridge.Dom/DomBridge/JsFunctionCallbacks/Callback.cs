using Broiler.HtmlBridge.Jseal;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

// The five propagation-control callbacks of the synthetic window event: stopPropagation,
// stopImmediatePropagation, preventDefault, and the legacy cancelBubble/returnValue setters.
//
// WHY THESE STILL TAKE AN ENGINE ARGUMENT FRAME. Their only caller is DispatchWindowEvent in
// DomBridge.WindowLoad.cs, which builds the event object with the engine and installs each of these as
// a DomFunction over `ref` locals it owns. A migrated body would need `in JsCall`, and there is no
// adapter between two call frames — only between two object types — so the signature is pinned by that
// call site and moves when it does. Features/LegacyEventBinding.cs is the same five operations already
// migrated, over local functions closing on the same state, and is the shape this becomes.
//
// What did move is everything inside the bodies: the event's own properties are read and written
// through the realm, so nothing here names a property key or a boolean of the engine's.
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
        // An absent `cancelable` reads as an absent value and is not truthy, which is the same answer
        // the engine-typed `!= null && .BooleanValue` pair gave.
        var handle = Dom.Runtime.JsInterop.FromEngineObject(evt);
        if (!currentListenerPassive && Realm.GetProperty(handle, "cancelable").AsBoolean)
        {
            prevented = true;
            Realm.SetProperty(handle, "defaultPrevented", JsValue.True);
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
        var handle = Dom.Runtime.JsInterop.FromEngineObject(evt);
        if (setArgs.Length > 0 && !setArgs[0].BooleanValue && !currentListenerPassive &&
            Realm.GetProperty(handle, "cancelable").AsBoolean)
        {
            prevented = true;
            Realm.SetProperty(handle, "defaultPrevented", JsValue.True);
        }

        return JSUndefined.Value;
    }

}
