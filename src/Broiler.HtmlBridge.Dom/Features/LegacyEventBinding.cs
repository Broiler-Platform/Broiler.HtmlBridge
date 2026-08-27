using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <c>document.createEvent(type)</c> — the legacy DOM Events Level 3 factory that returns a plain
/// event object pre-populated with the union of UI/Mouse/Keyboard/Wheel/Custom event fields and
/// the legacy <c>init*Event</c> / propagation-control methods. Co-located as an HtmlBridge feature
/// module (Phase 3). It builds a self-contained JS object with closures over its own state and
/// touches no bridge instance state, so — like ConsoleBinding / CryptoBinding — it is a pure static
/// class with no host contract. Previously the bridge's JsRegistrationCreateEvent033Core in the
/// shared JsFunctionCallbacks/Registration.cs grab-bag.
/// </summary>
internal static class LegacyEventBinding
{
    public static JSValue Create(in Arguments a)
    {
        var evt = new JSObject();
        var legacyCancelBubble = false;

        evt.FastAddValue("type", new JSString(string.Empty), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("bubbles", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("cancelable", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("defaultPrevented", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("target", JSNull.Value, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("currentTarget", JSNull.Value, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("srcElement", JSNull.Value, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("eventPhase", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("isTrusted", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("timeStamp", new JSNumber(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("detail", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("view", JSNull.Value, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("screenX", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("screenY", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("clientX", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("clientY", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("x", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("y", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("ctrlKey", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("altKey", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("shiftKey", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("metaKey", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("key", new JSString(string.Empty), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("location", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("repeat", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("keyCode", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("charCode", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("which", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("button", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("buttons", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("deltaX", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("deltaY", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("deltaZ", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("deltaMode", new JSNumber(0), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("relatedTarget", JSNull.Value, JSPropertyAttributes.EnumerableConfigurableValue);

        JSValue JsRegistrationStopPropagation018(in Arguments _)
        {
            legacyCancelBubble = true;
            return JSUndefined.Value;
        }

        evt.FastAddValue("stopPropagation", new DomFunction(JsRegistrationStopPropagation018, "stopPropagation", 0), JSPropertyAttributes.EnumerableConfigurableValue);
        JSValue JsRegistrationStopImmediatePropagation019(in Arguments _)
        {
            legacyCancelBubble = true;
            return JSUndefined.Value;
        }

        evt.FastAddValue("stopImmediatePropagation", new DomFunction(JsRegistrationStopImmediatePropagation019, "stopImmediatePropagation", 0), JSPropertyAttributes.EnumerableConfigurableValue);
        JSValue JsRegistrationPreventDefault020(in Arguments _)
        {
            var cancelable = evt[(KeyString)"cancelable"];
            if (cancelable != null && cancelable.BooleanValue)
                evt[(KeyString)"defaultPrevented"] = JSBoolean.True;
            return JSUndefined.Value;
        }

        evt.FastAddValue("preventDefault", new DomFunction(JsRegistrationPreventDefault020, "preventDefault", 0), JSPropertyAttributes.EnumerableConfigurableValue);
        JSValue JsRegistrationGetCancelBubble021(in Arguments _)
        {
            return legacyCancelBubble ? JSBoolean.True : JSBoolean.False;
        }

        JSValue JsRegistrationSetCancelBubble022(in Arguments setArgs)
        {
            if (setArgs.Length > 0 && setArgs[0].BooleanValue)
                legacyCancelBubble = true;
            return JSUndefined.Value;
        }

        evt.FastAddProperty("cancelBubble", new DomFunction(JsRegistrationGetCancelBubble021, "get cancelBubble"), new DomFunction(JsRegistrationSetCancelBubble022, "set cancelBubble"), JSPropertyAttributes.EnumerableConfigurableProperty);
        JSValue JsRegistrationGetReturnValue023(in Arguments _)
        {
            return evt[(KeyString)"defaultPrevented"].BooleanValue ? JSBoolean.False : JSBoolean.True;
        }

        JSValue JsRegistrationSetReturnValue024(in Arguments setArgs)
        {
            var cancelable = evt[(KeyString)"cancelable"];
            if (setArgs.Length > 0 && !setArgs[0].BooleanValue && cancelable != null && cancelable.BooleanValue)
                evt[(KeyString)"defaultPrevented"] = JSBoolean.True;
            return JSUndefined.Value;
        }

        evt.FastAddProperty("returnValue", new DomFunction(JsRegistrationGetReturnValue023, "get returnValue"), new DomFunction(JsRegistrationSetReturnValue024, "set returnValue"), JSPropertyAttributes.EnumerableConfigurableProperty);
        JSValue JsRegistrationInitEvent025(in Arguments initArgs)
        {
            if (initArgs.Length > 0)
                evt[(KeyString)"type"] = new JSString(initArgs[0].ToString());
            if (initArgs.Length > 1)
                evt[(KeyString)"bubbles"] = initArgs[1].BooleanValue ? JSBoolean.True : JSBoolean.False;
            if (initArgs.Length > 2)
                evt[(KeyString)"cancelable"] = initArgs[2].BooleanValue ? JSBoolean.True : JSBoolean.False;
            return JSUndefined.Value;
        }

        evt.FastAddValue("initEvent", new DomFunction(JsRegistrationInitEvent025, "initEvent", 3), JSPropertyAttributes.EnumerableConfigurableValue);
        JSValue JsRegistrationInitUIEvent026(in Arguments initArgs)
        {
            if (initArgs.Length > 0)
                evt[(KeyString)"type"] = new JSString(initArgs[0].ToString());
            if (initArgs.Length > 1)
                evt[(KeyString)"bubbles"] = initArgs[1].BooleanValue ? JSBoolean.True : JSBoolean.False;
            if (initArgs.Length > 2)
                evt[(KeyString)"cancelable"] = initArgs[2].BooleanValue ? JSBoolean.True : JSBoolean.False;
            if (initArgs.Length > 3)
                evt[(KeyString)"view"] = initArgs[3];
            if (initArgs.Length > 4)
                evt[(KeyString)"detail"] = initArgs[4];
            return JSUndefined.Value;
        }

        evt.FastAddValue("initUIEvent", new DomFunction(JsRegistrationInitUIEvent026, "initUIEvent", 5), JSPropertyAttributes.EnumerableConfigurableValue);
        JSValue JsRegistrationInitInputEvent027(in Arguments initArgs)
        {
            if (initArgs.Length > 0)
                evt[(KeyString)"type"] = new JSString(initArgs[0].ToString());
            if (initArgs.Length > 1)
                evt[(KeyString)"bubbles"] = initArgs[1].BooleanValue ? JSBoolean.True : JSBoolean.False;
            if (initArgs.Length > 2)
                evt[(KeyString)"cancelable"] = initArgs[2].BooleanValue ? JSBoolean.True : JSBoolean.False;
            if (initArgs.Length > 3)
                evt[(KeyString)"view"] = initArgs[3];
            if (initArgs.Length > 4)
                evt[(KeyString)"data"] = initArgs[4];
            if (initArgs.Length > 5)
                evt[(KeyString)"inputType"] = new JSString(initArgs[5].ToString());
            if (initArgs.Length > 6)
                evt[(KeyString)"isComposing"] = initArgs[6].BooleanValue ? JSBoolean.True : JSBoolean.False;
            return JSUndefined.Value;
        }

        evt.FastAddValue("initInputEvent", new DomFunction(JsRegistrationInitInputEvent027, "initInputEvent", 7), JSPropertyAttributes.EnumerableConfigurableValue);
        JSValue JsRegistrationInitCustomEvent028(in Arguments initArgs)
        {
            if (initArgs.Length > 0)
                evt[(KeyString)"type"] = new JSString(initArgs[0].ToString());
            if (initArgs.Length > 1)
                evt[(KeyString)"bubbles"] = initArgs[1].BooleanValue ? JSBoolean.True : JSBoolean.False;
            if (initArgs.Length > 2)
                evt[(KeyString)"cancelable"] = initArgs[2].BooleanValue ? JSBoolean.True : JSBoolean.False;
            evt[(KeyString)"detail"] = initArgs.Length > 3 ? initArgs[3] : JSNull.Value;
            return JSUndefined.Value;
        }

        evt.FastAddValue("initCustomEvent", new DomFunction(JsRegistrationInitCustomEvent028, "initCustomEvent", 4), JSPropertyAttributes.EnumerableConfigurableValue);
        JSValue JsRegistrationInitFocusEvent029(in Arguments initArgs)
        {
            if (initArgs.Length > 0)
                evt[(KeyString)"type"] = new JSString(initArgs[0].ToString());
            if (initArgs.Length > 1)
                evt[(KeyString)"bubbles"] = initArgs[1].BooleanValue ? JSBoolean.True : JSBoolean.False;
            if (initArgs.Length > 2)
                evt[(KeyString)"cancelable"] = initArgs[2].BooleanValue ? JSBoolean.True : JSBoolean.False;
            if (initArgs.Length > 3)
                evt[(KeyString)"view"] = initArgs[3];
            if (initArgs.Length > 4)
                evt[(KeyString)"detail"] = new JSNumber(initArgs[4].DoubleValue);
            if (initArgs.Length > 5)
                evt[(KeyString)"relatedTarget"] = initArgs[5];
            return JSUndefined.Value;
        }

        evt.FastAddValue("initFocusEvent", new DomFunction(JsRegistrationInitFocusEvent029, "initFocusEvent", 6), JSPropertyAttributes.EnumerableConfigurableValue);
        JSValue JsRegistrationInitKeyboardEvent030(in Arguments initArgs)
        {
            if (initArgs.Length > 0)
                evt[(KeyString)"type"] = new JSString(initArgs[0].ToString());
            if (initArgs.Length > 1)
                evt[(KeyString)"bubbles"] = initArgs[1].BooleanValue ? JSBoolean.True : JSBoolean.False;
            if (initArgs.Length > 2)
                evt[(KeyString)"cancelable"] = initArgs[2].BooleanValue ? JSBoolean.True : JSBoolean.False;
            if (initArgs.Length > 3)
                evt[(KeyString)"view"] = initArgs[3];
            if (initArgs.Length > 4)
                evt[(KeyString)"key"] = new JSString(initArgs[4].ToString());
            if (initArgs.Length > 5)
                evt[(KeyString)"location"] = new JSNumber(initArgs[5].DoubleValue);
            if (initArgs.Length > 6)
                evt[(KeyString)"ctrlKey"] = initArgs[6].BooleanValue ? JSBoolean.True : JSBoolean.False;
            if (initArgs.Length > 7)
                evt[(KeyString)"altKey"] = initArgs[7].BooleanValue ? JSBoolean.True : JSBoolean.False;
            if (initArgs.Length > 8)
                evt[(KeyString)"shiftKey"] = initArgs[8].BooleanValue ? JSBoolean.True : JSBoolean.False;
            if (initArgs.Length > 9)
                evt[(KeyString)"metaKey"] = initArgs[9].BooleanValue ? JSBoolean.True : JSBoolean.False;
            if (initArgs.Length > 10)
                evt[(KeyString)"repeat"] = initArgs[10].BooleanValue ? JSBoolean.True : JSBoolean.False;
            if (initArgs.Length > 11)
            {
                var keyCode = initArgs[11].DoubleValue;
                evt[(KeyString)"keyCode"] = new JSNumber(keyCode);
                evt[(KeyString)"which"] = new JSNumber(keyCode);
            }

            if (initArgs.Length > 12)
            {
                var charCode = initArgs[12].DoubleValue;
                evt[(KeyString)"charCode"] = new JSNumber(charCode);
                if (charCode != 0)
                    evt[(KeyString)"which"] = new JSNumber(charCode);
            }

            return JSUndefined.Value;
        }

        evt.FastAddValue("initKeyboardEvent", new DomFunction(JsRegistrationInitKeyboardEvent030, "initKeyboardEvent", 13), JSPropertyAttributes.EnumerableConfigurableValue);
        JSValue JsRegistrationInitMouseEvent031(in Arguments initArgs)
        {
            if (initArgs.Length > 0)
                evt[(KeyString)"type"] = new JSString(initArgs[0].ToString());
            if (initArgs.Length > 1)
                evt[(KeyString)"bubbles"] = initArgs[1].BooleanValue ? JSBoolean.True : JSBoolean.False;
            if (initArgs.Length > 2)
                evt[(KeyString)"cancelable"] = initArgs[2].BooleanValue ? JSBoolean.True : JSBoolean.False;
            if (initArgs.Length > 3)
                evt[(KeyString)"view"] = initArgs[3];
            if (initArgs.Length > 4)
                evt[(KeyString)"detail"] = new JSNumber(initArgs[4].DoubleValue);
            if (initArgs.Length > 5)
                evt[(KeyString)"screenX"] = new JSNumber(initArgs[5].DoubleValue);
            if (initArgs.Length > 6)
                evt[(KeyString)"screenY"] = new JSNumber(initArgs[6].DoubleValue);
            if (initArgs.Length > 7)
            {
                evt[(KeyString)"clientX"] = new JSNumber(initArgs[7].DoubleValue);
                evt[(KeyString)"x"] = new JSNumber(initArgs[7].DoubleValue);
            }

            if (initArgs.Length > 8)
            {
                evt[(KeyString)"clientY"] = new JSNumber(initArgs[8].DoubleValue);
                evt[(KeyString)"y"] = new JSNumber(initArgs[8].DoubleValue);
            }

            if (initArgs.Length > 9)
                evt[(KeyString)"ctrlKey"] = initArgs[9].BooleanValue ? JSBoolean.True : JSBoolean.False;
            if (initArgs.Length > 10)
                evt[(KeyString)"altKey"] = initArgs[10].BooleanValue ? JSBoolean.True : JSBoolean.False;
            if (initArgs.Length > 11)
                evt[(KeyString)"shiftKey"] = initArgs[11].BooleanValue ? JSBoolean.True : JSBoolean.False;
            if (initArgs.Length > 12)
                evt[(KeyString)"metaKey"] = initArgs[12].BooleanValue ? JSBoolean.True : JSBoolean.False;
            if (initArgs.Length > 13)
            {
                var button = initArgs[13].DoubleValue;
                evt[(KeyString)"button"] = new JSNumber(button);
                evt[(KeyString)"buttons"] = new JSNumber(button switch
                {
                    0 => 1,
                    1 => 4,
                    2 => 2,
                    _ => 0
                });
            }

            if (initArgs.Length > 14)
                evt[(KeyString)"relatedTarget"] = initArgs[14];
            return JSUndefined.Value;
        }

        evt.FastAddValue("initMouseEvent", new DomFunction(JsRegistrationInitMouseEvent031, "initMouseEvent", 15), JSPropertyAttributes.EnumerableConfigurableValue);
        JSValue JsRegistrationInitWheelEvent032(in Arguments initArgs)
        {
            if (initArgs.Length > 0)
                evt[(KeyString)"type"] = new JSString(initArgs[0].ToString());
            if (initArgs.Length > 1)
                evt[(KeyString)"bubbles"] = initArgs[1].BooleanValue ? JSBoolean.True : JSBoolean.False;
            if (initArgs.Length > 2)
                evt[(KeyString)"cancelable"] = initArgs[2].BooleanValue ? JSBoolean.True : JSBoolean.False;
            if (initArgs.Length > 3)
                evt[(KeyString)"view"] = initArgs[3];
            if (initArgs.Length > 4)
                evt[(KeyString)"detail"] = new JSNumber(initArgs[4].DoubleValue);
            if (initArgs.Length > 5)
                evt[(KeyString)"screenX"] = new JSNumber(initArgs[5].DoubleValue);
            if (initArgs.Length > 6)
                evt[(KeyString)"screenY"] = new JSNumber(initArgs[6].DoubleValue);
            if (initArgs.Length > 7)
            {
                evt[(KeyString)"clientX"] = new JSNumber(initArgs[7].DoubleValue);
                evt[(KeyString)"x"] = new JSNumber(initArgs[7].DoubleValue);
            }

            if (initArgs.Length > 8)
            {
                evt[(KeyString)"clientY"] = new JSNumber(initArgs[8].DoubleValue);
                evt[(KeyString)"y"] = new JSNumber(initArgs[8].DoubleValue);
            }

            if (initArgs.Length > 9)
                evt[(KeyString)"button"] = new JSNumber(initArgs[9].DoubleValue);
            if (initArgs.Length > 10)
                evt[(KeyString)"relatedTarget"] = initArgs[10];
            if (initArgs.Length > 11)
            {
                var modifiers = initArgs[11].ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                evt[(KeyString)"ctrlKey"] = Array.Exists(modifiers, m => string.Equals(m, "Control", StringComparison.OrdinalIgnoreCase)) ? JSBoolean.True : JSBoolean.False;
                evt[(KeyString)"altKey"] = Array.Exists(modifiers, m => string.Equals(m, "Alt", StringComparison.OrdinalIgnoreCase)) ? JSBoolean.True : JSBoolean.False;
                evt[(KeyString)"shiftKey"] = Array.Exists(modifiers, m => string.Equals(m, "Shift", StringComparison.OrdinalIgnoreCase)) ? JSBoolean.True : JSBoolean.False;
                evt[(KeyString)"metaKey"] = Array.Exists(modifiers, m => string.Equals(m, "Meta", StringComparison.OrdinalIgnoreCase)) ? JSBoolean.True : JSBoolean.False;
            }

            if (initArgs.Length > 12)
                evt[(KeyString)"deltaX"] = new JSNumber(initArgs[12].DoubleValue);
            if (initArgs.Length > 13)
                evt[(KeyString)"deltaY"] = new JSNumber(initArgs[13].DoubleValue);
            if (initArgs.Length > 14)
                evt[(KeyString)"deltaZ"] = new JSNumber(initArgs[14].DoubleValue);
            if (initArgs.Length > 15)
                evt[(KeyString)"deltaMode"] = new JSNumber(initArgs[15].DoubleValue);
            return JSUndefined.Value;
        }

        evt.FastAddValue("initWheelEvent", new DomFunction(JsRegistrationInitWheelEvent032, "initWheelEvent", 16), JSPropertyAttributes.EnumerableConfigurableValue);
        return evt;
    }
}
