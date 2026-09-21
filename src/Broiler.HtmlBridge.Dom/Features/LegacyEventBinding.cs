using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <c>document.createEvent(type)</c> — the legacy DOM Events Level 3 factory that returns a plain
/// event object pre-populated with the union of UI/Mouse/Keyboard/Wheel/Custom event fields and
/// the legacy <c>init*Event</c> / propagation-control methods. Co-located as an HtmlBridge feature
/// module. It builds a self-contained JS object with closures over its own state and
/// touches no bridge instance state, so — like ConsoleBinding / CryptoBinding — it is a pure static
/// class with no host contract.
/// </summary>
/// <remarks>
/// <para>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine
/// type. The event object and the flag behind <c>cancelBubble</c> are captured by the operation
/// closures; the object is minted by the realm, its members are installed through
/// <see cref="IJsMembers"/>, and its own fields are read and written through the realm.
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

        realm.DefineMethod(evt, "stopPropagation", 0, StopPropagation);
        realm.DefineMethod(evt, "stopImmediatePropagation", 0, StopImmediatePropagation);
        realm.DefineMethod(evt, "preventDefault", 0, PreventDefault);
        realm.DefineAccessor(evt, "cancelBubble", GetCancelBubble, SetCancelBubble);
        realm.DefineAccessor(evt, "returnValue", GetReturnValue, SetReturnValue);
        realm.DefineMethod(evt, "initEvent", 3, InitEvent);
        realm.DefineMethod(evt, "initUIEvent", 5, InitUIEvent);
        realm.DefineMethod(evt, "initInputEvent", 7, InitInputEvent);
        realm.DefineMethod(evt, "initCustomEvent", 4, InitCustomEvent);
        realm.DefineMethod(evt, "initFocusEvent", 6, InitFocusEvent);
        realm.DefineMethod(evt, "initKeyboardEvent", 13, InitKeyboardEvent);
        realm.DefineMethod(evt, "initMouseEvent", 15, InitMouseEvent);
        realm.DefineMethod(evt, "initWheelEvent", 16, InitWheelEvent);

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
            Raw(in initCall, 3, "view");
            // `detail` arrives uncoerced here, unlike the Focus/Mouse/Wheel forms below.
            Raw(in initCall, 4, "detail");
            return JsValue.Undefined;
        }

        JsValue InitInputEvent(in JsCall initCall)
        {
            InitBase(in initCall);
            Raw(in initCall, 3, "view");
            Raw(in initCall, 4, "data");
            Str(in initCall, 5, "inputType");
            Bool(in initCall, 6, "isComposing");
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
            Raw(in initCall, 3, "view");
            Num(in initCall, 4, "detail");
            Raw(in initCall, 5, "relatedTarget");
            return JsValue.Undefined;
        }

        JsValue InitKeyboardEvent(in JsCall initCall)
        {
            InitBase(in initCall);
            Raw(in initCall, 3, "view");
            Str(in initCall, 4, "key");
            Num(in initCall, 5, "location");
            Bool(in initCall, 6, "ctrlKey");
            Bool(in initCall, 7, "altKey");
            Bool(in initCall, 8, "shiftKey");
            Bool(in initCall, 9, "metaKey");
            Bool(in initCall, 10, "repeat");
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
            InitPointerBase(in initCall);
            Bool(in initCall, 9, "ctrlKey");
            Bool(in initCall, 10, "altKey");
            Bool(in initCall, 11, "shiftKey");
            Bool(in initCall, 12, "metaKey");
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

            Raw(in initCall, 14, "relatedTarget");
            return JsValue.Undefined;
        }

        JsValue InitWheelEvent(in JsCall initCall)
        {
            InitBase(in initCall);
            InitPointerBase(in initCall);
            // The wheel form diverges from the mouse form here: `button` is a number at slot 9 where
            // mouse takes ctrlKey, and `relatedTarget` sits at 10 rather than at 14.
            Num(in initCall, 9, "button");
            Raw(in initCall, 10, "relatedTarget");
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

            Num(in initCall, 12, "deltaX");
            Num(in initCall, 13, "deltaY");
            Num(in initCall, 14, "deltaZ");
            Num(in initCall, 15, "deltaMode");
            return JsValue.Undefined;
        }

        // (type, bubbles, cancelable) — the first three arguments every one of the eight init forms
        // takes, and the eight copies of this that were written out in full.
        void InitBase(in JsCall initCall)
        {
            Str(in initCall, 0, "type");
            Bool(in initCall, 1, "bubbles");
            Bool(in initCall, 2, "cancelable");
        }

        // (view, detail, screenX, screenY, clientX/x, clientY/y) — argument slots 3-8, which
        // initMouseEvent and initWheelEvent spell identically before diverging at slot 9. Not named
        // for coordinates alone: it also writes `view` and `detail`.
        void InitPointerBase(in JsCall initCall)
        {
            Raw(in initCall, 3, "view");
            Num(in initCall, 4, "detail");
            Num(in initCall, 5, "screenX");
            Num(in initCall, 6, "screenY");
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
        }

        // The four coercions an init* argument slot can carry, each written once instead of at the
        // 39 sites that spelled out the same `if (length > i) SetProperty(...)` pair. They stay four
        // separate helpers rather than one taking a ready-made JsValue: C# evaluates an argument
        // eagerly, so a single `Set(call, i, name, value)` would coerce an index that is not there.
        //
        // Each reads the realm off the call it was handed, never the realm captured by Create: this
        // factory is also installed on a sub-document, where the realm invoking an init* method need
        // not be the one that minted the event.

        // Raw — the argument as it arrived, with no coercion at all.
        void Raw(in JsCall initCall, int index, string name)
        {
            if (initCall.Length > index)
                initCall.Realm.SetProperty(evt, name, initCall[index]);
        }

        // Str — ECMAScript ToString of the argument.
        void Str(in JsCall initCall, int index, string name)
        {
            if (initCall.Length > index)
                initCall.Realm.SetProperty(evt, name, JsValue.String(initCall.Realm.ToJsString(initCall[index])));
        }

        // Num — ECMAScript ToNumber of the argument; see the class remarks on why not AsNumber.
        void Num(in JsCall initCall, int index, string name)
        {
            if (initCall.Length > index)
                initCall.Realm.SetProperty(evt, name, JsValue.Number(initCall.Realm.ToNumber(initCall[index])));
        }

        // Bool — the argument's truthiness.
        void Bool(in JsCall initCall, int index, string name)
        {
            if (initCall.Length > index)
                initCall.Realm.SetProperty(evt, name, JsValue.Boolean(initCall[index].AsBoolean));
        }
    }
}
