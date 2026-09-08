using Broiler.HtmlBridge.Jseal;

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
/// <remarks>
/// <para>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine
/// type. The event object and the flag behind <c>cancelBubble</c> are still captured by the operation
/// closures exactly as before; what changed is that the object is minted by the realm, its members are
/// installed through <see cref="IJsMembers"/>, and its own fields are read and written through the
/// realm rather than through the engine's indexer.
/// </para>
/// <para>
/// <b>Every numeric argument goes through the realm's <c>ToNumber</c>, not the handle's
/// <c>AsNumber</c>.</b> These are page-supplied <c>init*Event</c> arguments and the engine's
/// <c>DoubleValue</c> — which this code called on each of them — <em>is</em> ECMAScript
/// <c>ToNumber</c>: it coerces a numeric string and reaches a <c>valueOf</c> the page wrote. A page
/// calling <c>initMouseEvent('click', true, true, window, 1, '10', '20', …)</c> is common enough that
/// answering <c>NaN</c> for those would be a silent behaviour change rather than a tightening.
/// </para>
/// </remarks>
internal static class LegacyEventBinding
{
    public static JsValue Create(in JsCall call)
    {
        var realm = call.Realm;
        var evt = realm.NewObject();
        var legacyCancelBubble = false;

        realm.DefineValue(evt, "type", JsValue.String(string.Empty));
        realm.DefineValue(evt, "bubbles", JsValue.False);
        realm.DefineValue(evt, "cancelable", JsValue.False);
        realm.DefineValue(evt, "defaultPrevented", JsValue.False);
        realm.DefineValue(evt, "target", JsValue.Null);
        realm.DefineValue(evt, "currentTarget", JsValue.Null);
        realm.DefineValue(evt, "srcElement", JsValue.Null);
        realm.DefineValue(evt, "eventPhase", JsValue.Number(0));
        realm.DefineValue(evt, "isTrusted", JsValue.False);
        realm.DefineValue(evt, "timeStamp", JsValue.Number(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
        realm.DefineValue(evt, "detail", JsValue.Number(0));
        realm.DefineValue(evt, "view", JsValue.Null);
        realm.DefineValue(evt, "screenX", JsValue.Number(0));
        realm.DefineValue(evt, "screenY", JsValue.Number(0));
        realm.DefineValue(evt, "clientX", JsValue.Number(0));
        realm.DefineValue(evt, "clientY", JsValue.Number(0));
        realm.DefineValue(evt, "x", JsValue.Number(0));
        realm.DefineValue(evt, "y", JsValue.Number(0));
        realm.DefineValue(evt, "ctrlKey", JsValue.False);
        realm.DefineValue(evt, "altKey", JsValue.False);
        realm.DefineValue(evt, "shiftKey", JsValue.False);
        realm.DefineValue(evt, "metaKey", JsValue.False);
        realm.DefineValue(evt, "key", JsValue.String(string.Empty));
        realm.DefineValue(evt, "location", JsValue.Number(0));
        realm.DefineValue(evt, "repeat", JsValue.False);
        realm.DefineValue(evt, "keyCode", JsValue.Number(0));
        realm.DefineValue(evt, "charCode", JsValue.Number(0));
        realm.DefineValue(evt, "which", JsValue.Number(0));
        realm.DefineValue(evt, "button", JsValue.Number(0));
        realm.DefineValue(evt, "buttons", JsValue.Number(0));
        realm.DefineValue(evt, "deltaX", JsValue.Number(0));
        realm.DefineValue(evt, "deltaY", JsValue.Number(0));
        realm.DefineValue(evt, "deltaZ", JsValue.Number(0));
        realm.DefineValue(evt, "deltaMode", JsValue.Number(0));
        realm.DefineValue(evt, "relatedTarget", JsValue.Null);

        realm.DefineValue(evt, "stopPropagation", realm.NewMethod("stopPropagation", StopPropagation, 0));
        realm.DefineValue(evt, "stopImmediatePropagation", realm.NewMethod("stopImmediatePropagation", StopImmediatePropagation, 0));
        realm.DefineValue(evt, "preventDefault", realm.NewMethod("preventDefault", PreventDefault, 0));
        realm.DefineAccessor(evt, "cancelBubble", GetCancelBubble, SetCancelBubble);
        realm.DefineAccessor(evt, "returnValue", GetReturnValue, SetReturnValue);
        realm.DefineValue(evt, "initEvent", realm.NewMethod("initEvent", InitEvent, 3));
        realm.DefineValue(evt, "initUIEvent", realm.NewMethod("initUIEvent", InitUIEvent, 5));
        realm.DefineValue(evt, "initInputEvent", realm.NewMethod("initInputEvent", InitInputEvent, 7));
        realm.DefineValue(evt, "initCustomEvent", realm.NewMethod("initCustomEvent", InitCustomEvent, 4));
        realm.DefineValue(evt, "initFocusEvent", realm.NewMethod("initFocusEvent", InitFocusEvent, 6));
        realm.DefineValue(evt, "initKeyboardEvent", realm.NewMethod("initKeyboardEvent", InitKeyboardEvent, 13));
        realm.DefineValue(evt, "initMouseEvent", realm.NewMethod("initMouseEvent", InitMouseEvent, 15));
        realm.DefineValue(evt, "initWheelEvent", realm.NewMethod("initWheelEvent", InitWheelEvent, 16));

        return evt;

        JsValue StopPropagation(in JsCall _)
        {
            legacyCancelBubble = true;
            return JsValue.Undefined;
        }

        JsValue StopImmediatePropagation(in JsCall _)
        {
            legacyCancelBubble = true;
            return JsValue.Undefined;
        }

        JsValue PreventDefault(in JsCall preventCall)
        {
            // An absent `cancelable` reads as an absent value and is not truthy, which is the same
            // answer the engine-typed `!= null && .BooleanValue` pair gave.
            if (preventCall.Realm.GetProperty(evt, "cancelable").AsBoolean)
                preventCall.Realm.SetProperty(evt, "defaultPrevented", JsValue.True);
            return JsValue.Undefined;
        }

        JsValue GetCancelBubble(in JsCall _) => JsValue.Boolean(legacyCancelBubble);

        JsValue SetCancelBubble(in JsCall setCall)
        {
            // Assigning a falsy value does not clear the flag. That is the legacy property's
            // documented one-way behaviour, and it is what this did before.
            if (setCall.Length > 0 && setCall[0].AsBoolean)
                legacyCancelBubble = true;
            return JsValue.Undefined;
        }

        JsValue GetReturnValue(in JsCall getCall) =>
            JsValue.Boolean(!getCall.Realm.GetProperty(evt, "defaultPrevented").AsBoolean);

        JsValue SetReturnValue(in JsCall setCall)
        {
            // Read first and test second, as this did before: were a page to redefine `cancelable` as
            // an accessor, its getter would run on every assignment either way.
            var cancelable = setCall.Realm.GetProperty(evt, "cancelable").AsBoolean;
            if (setCall.Length > 0 && !setCall[0].AsBoolean && cancelable)
                setCall.Realm.SetProperty(evt, "defaultPrevented", JsValue.True);
            return JsValue.Undefined;
        }

        JsValue InitEvent(in JsCall initCall)
        {
            InitBase(in initCall);
            return JsValue.Undefined;
        }

        JsValue InitUIEvent(in JsCall initCall)
        {
            InitBase(in initCall);
            if (initCall.Length > 3)
                initCall.Realm.SetProperty(evt, "view", initCall[3]);
            if (initCall.Length > 4)
                initCall.Realm.SetProperty(evt, "detail", initCall[4]);
            return JsValue.Undefined;
        }

        JsValue InitInputEvent(in JsCall initCall)
        {
            InitBase(in initCall);
            if (initCall.Length > 3)
                initCall.Realm.SetProperty(evt, "view", initCall[3]);
            if (initCall.Length > 4)
                initCall.Realm.SetProperty(evt, "data", initCall[4]);
            if (initCall.Length > 5)
                initCall.Realm.SetProperty(evt, "inputType", JsValue.String(initCall.Realm.ToJsString(initCall[5])));
            if (initCall.Length > 6)
                initCall.Realm.SetProperty(evt, "isComposing", JsValue.Boolean(initCall[6].AsBoolean));
            return JsValue.Undefined;
        }

        JsValue InitCustomEvent(in JsCall initCall)
        {
            InitBase(in initCall);
            // Unconditional, unlike every other field: an initCustomEvent with no detail argument
            // resets detail to null rather than leaving the previous value.
            initCall.Realm.SetProperty(evt, "detail", initCall.Length > 3 ? initCall[3] : JsValue.Null);
            return JsValue.Undefined;
        }

        JsValue InitFocusEvent(in JsCall initCall)
        {
            InitBase(in initCall);
            if (initCall.Length > 3)
                initCall.Realm.SetProperty(evt, "view", initCall[3]);
            if (initCall.Length > 4)
                initCall.Realm.SetProperty(evt, "detail", JsValue.Number(initCall.Realm.ToNumber(initCall[4])));
            if (initCall.Length > 5)
                initCall.Realm.SetProperty(evt, "relatedTarget", initCall[5]);
            return JsValue.Undefined;
        }

        JsValue InitKeyboardEvent(in JsCall initCall)
        {
            InitBase(in initCall);
            if (initCall.Length > 3)
                initCall.Realm.SetProperty(evt, "view", initCall[3]);
            if (initCall.Length > 4)
                initCall.Realm.SetProperty(evt, "key", JsValue.String(initCall.Realm.ToJsString(initCall[4])));
            if (initCall.Length > 5)
                initCall.Realm.SetProperty(evt, "location", JsValue.Number(initCall.Realm.ToNumber(initCall[5])));
            if (initCall.Length > 6)
                initCall.Realm.SetProperty(evt, "ctrlKey", JsValue.Boolean(initCall[6].AsBoolean));
            if (initCall.Length > 7)
                initCall.Realm.SetProperty(evt, "altKey", JsValue.Boolean(initCall[7].AsBoolean));
            if (initCall.Length > 8)
                initCall.Realm.SetProperty(evt, "shiftKey", JsValue.Boolean(initCall[8].AsBoolean));
            if (initCall.Length > 9)
                initCall.Realm.SetProperty(evt, "metaKey", JsValue.Boolean(initCall[9].AsBoolean));
            if (initCall.Length > 10)
                initCall.Realm.SetProperty(evt, "repeat", JsValue.Boolean(initCall[10].AsBoolean));
            if (initCall.Length > 11)
            {
                var keyCode = initCall.Realm.ToNumber(initCall[11]);
                initCall.Realm.SetProperty(evt, "keyCode", JsValue.Number(keyCode));
                initCall.Realm.SetProperty(evt, "which", JsValue.Number(keyCode));
            }

            if (initCall.Length > 12)
            {
                var charCode = initCall.Realm.ToNumber(initCall[12]);
                initCall.Realm.SetProperty(evt, "charCode", JsValue.Number(charCode));
                // A zero charCode leaves `which` reporting the keyCode above — a key press with no
                // character does not overwrite it.
                if (charCode != 0)
                    initCall.Realm.SetProperty(evt, "which", JsValue.Number(charCode));
            }

            return JsValue.Undefined;
        }

        JsValue InitMouseEvent(in JsCall initCall)
        {
            InitBase(in initCall);
            if (initCall.Length > 3)
                initCall.Realm.SetProperty(evt, "view", initCall[3]);
            if (initCall.Length > 4)
                initCall.Realm.SetProperty(evt, "detail", JsValue.Number(initCall.Realm.ToNumber(initCall[4])));
            if (initCall.Length > 5)
                initCall.Realm.SetProperty(evt, "screenX", JsValue.Number(initCall.Realm.ToNumber(initCall[5])));
            if (initCall.Length > 6)
                initCall.Realm.SetProperty(evt, "screenY", JsValue.Number(initCall.Realm.ToNumber(initCall[6])));
            if (initCall.Length > 7)
            {
                var clientX = initCall.Realm.ToNumber(initCall[7]);
                initCall.Realm.SetProperty(evt, "clientX", JsValue.Number(clientX));
                initCall.Realm.SetProperty(evt, "x", JsValue.Number(clientX));
            }

            if (initCall.Length > 8)
            {
                var clientY = initCall.Realm.ToNumber(initCall[8]);
                initCall.Realm.SetProperty(evt, "clientY", JsValue.Number(clientY));
                initCall.Realm.SetProperty(evt, "y", JsValue.Number(clientY));
            }

            if (initCall.Length > 9)
                initCall.Realm.SetProperty(evt, "ctrlKey", JsValue.Boolean(initCall[9].AsBoolean));
            if (initCall.Length > 10)
                initCall.Realm.SetProperty(evt, "altKey", JsValue.Boolean(initCall[10].AsBoolean));
            if (initCall.Length > 11)
                initCall.Realm.SetProperty(evt, "shiftKey", JsValue.Boolean(initCall[11].AsBoolean));
            if (initCall.Length > 12)
                initCall.Realm.SetProperty(evt, "metaKey", JsValue.Boolean(initCall[12].AsBoolean));
            if (initCall.Length > 13)
            {
                var button = initCall.Realm.ToNumber(initCall[13]);
                initCall.Realm.SetProperty(evt, "button", JsValue.Number(button));
                // The buttons bitmask the button index implies: primary is bit 0, middle bit 2,
                // secondary bit 1 — and anything else reports no button held.
                initCall.Realm.SetProperty(evt, "buttons", JsValue.Number(button switch
                {
                    0 => 1,
                    1 => 4,
                    2 => 2,
                    _ => 0
                }));
            }

            if (initCall.Length > 14)
                initCall.Realm.SetProperty(evt, "relatedTarget", initCall[14]);
            return JsValue.Undefined;
        }

        JsValue InitWheelEvent(in JsCall initCall)
        {
            InitBase(in initCall);
            if (initCall.Length > 3)
                initCall.Realm.SetProperty(evt, "view", initCall[3]);
            if (initCall.Length > 4)
                initCall.Realm.SetProperty(evt, "detail", JsValue.Number(initCall.Realm.ToNumber(initCall[4])));
            if (initCall.Length > 5)
                initCall.Realm.SetProperty(evt, "screenX", JsValue.Number(initCall.Realm.ToNumber(initCall[5])));
            if (initCall.Length > 6)
                initCall.Realm.SetProperty(evt, "screenY", JsValue.Number(initCall.Realm.ToNumber(initCall[6])));
            if (initCall.Length > 7)
            {
                var clientX = initCall.Realm.ToNumber(initCall[7]);
                initCall.Realm.SetProperty(evt, "clientX", JsValue.Number(clientX));
                initCall.Realm.SetProperty(evt, "x", JsValue.Number(clientX));
            }

            if (initCall.Length > 8)
            {
                var clientY = initCall.Realm.ToNumber(initCall[8]);
                initCall.Realm.SetProperty(evt, "clientY", JsValue.Number(clientY));
                initCall.Realm.SetProperty(evt, "y", JsValue.Number(clientY));
            }

            if (initCall.Length > 9)
                initCall.Realm.SetProperty(evt, "button", JsValue.Number(initCall.Realm.ToNumber(initCall[9])));
            if (initCall.Length > 10)
                initCall.Realm.SetProperty(evt, "relatedTarget", initCall[10]);
            if (initCall.Length > 11)
            {
                // The wheel form takes its modifiers as a whitespace-separated key list rather than as
                // four booleans, so each flag is the presence of a name in it.
                var modifiers = initCall.Realm.ToJsString(initCall[11])
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                initCall.Realm.SetProperty(evt, "ctrlKey", JsValue.Boolean(Array.Exists(modifiers, m => string.Equals(m, "Control", StringComparison.OrdinalIgnoreCase))));
                initCall.Realm.SetProperty(evt, "altKey", JsValue.Boolean(Array.Exists(modifiers, m => string.Equals(m, "Alt", StringComparison.OrdinalIgnoreCase))));
                initCall.Realm.SetProperty(evt, "shiftKey", JsValue.Boolean(Array.Exists(modifiers, m => string.Equals(m, "Shift", StringComparison.OrdinalIgnoreCase))));
                initCall.Realm.SetProperty(evt, "metaKey", JsValue.Boolean(Array.Exists(modifiers, m => string.Equals(m, "Meta", StringComparison.OrdinalIgnoreCase))));
            }

            if (initCall.Length > 12)
                initCall.Realm.SetProperty(evt, "deltaX", JsValue.Number(initCall.Realm.ToNumber(initCall[12])));
            if (initCall.Length > 13)
                initCall.Realm.SetProperty(evt, "deltaY", JsValue.Number(initCall.Realm.ToNumber(initCall[13])));
            if (initCall.Length > 14)
                initCall.Realm.SetProperty(evt, "deltaZ", JsValue.Number(initCall.Realm.ToNumber(initCall[14])));
            if (initCall.Length > 15)
                initCall.Realm.SetProperty(evt, "deltaMode", JsValue.Number(initCall.Realm.ToNumber(initCall[15])));
            return JsValue.Undefined;
        }

        // (type, bubbles, cancelable) — the first three arguments every one of the eight init forms
        // takes, and the eight copies of this that were written out in full.
        void InitBase(in JsCall initCall)
        {
            if (initCall.Length > 0)
                initCall.Realm.SetProperty(evt, "type", JsValue.String(initCall.Realm.ToJsString(initCall[0])));
            if (initCall.Length > 1)
                initCall.Realm.SetProperty(evt, "bubbles", JsValue.Boolean(initCall[1].AsBoolean));
            if (initCall.Length > 2)
                initCall.Realm.SetProperty(evt, "cancelable", JsValue.Boolean(initCall[2].AsBoolean));
        }
    }
}
