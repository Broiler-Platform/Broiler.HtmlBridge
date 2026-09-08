using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <see cref="SubDocumentBinding"/> — the legacy <c>document.createEvent(type)</c> factory and the
/// <c>initEvent</c>/<c>initUIEvent</c>/<c>initMouseEvent</c>/… mutator family it installs on the
/// returned event object. Self-contained: it builds a plain JS event object with no bridge coupling.
/// </summary>
/// <remarks>
/// Every mutator reads its arguments through the realm rather than the handle — <c>ToString</c> for a
/// type or a key, <c>ToNumber</c> for a coordinate — because that is the coercion these have always
/// performed: <c>initMouseEvent(…, "3", …)</c> passes a string where a number is expected, and the
/// engine's own numeric view of an argument is <c>ToNumber</c>. Truthiness is not an engine question,
/// so the boolean flags read the handle directly.
/// </remarks>
internal sealed partial class SubDocumentBinding
{
    private static JsValue CreateEvent(in JsCall call)
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

        JsValue StopPropagation(in JsCall __)
        {
            legacyCancelBubble = true;
            return JsValue.Undefined;
        }

        realm.DefineValue(evt, "stopPropagation", realm.NewMethod("stopPropagation", StopPropagation, 0));

        JsValue StopImmediatePropagation(in JsCall __)
        {
            legacyCancelBubble = true;
            return JsValue.Undefined;
        }

        realm.DefineValue(evt, "stopImmediatePropagation",
            realm.NewMethod("stopImmediatePropagation", StopImmediatePropagation, 0));

        JsValue PreventDefault(in JsCall preventCall)
        {
            preventCall.Realm.SetProperty(evt, "defaultPrevented", JsValue.True);
            return JsValue.Undefined;
        }

        realm.DefineValue(evt, "preventDefault", realm.NewMethod("preventDefault", PreventDefault, 0));

        JsValue GetCancelBubble(in JsCall __) => JsValue.Boolean(legacyCancelBubble);

        JsValue SetCancelBubble(in JsCall setCall)
        {
            if (setCall.Length > 0 && setCall[0].AsBoolean)
                legacyCancelBubble = true;
            return JsValue.Undefined;
        }

        realm.DefineAccessor(evt, "cancelBubble", GetCancelBubble, SetCancelBubble);

        JsValue GetReturnValue(in JsCall getCall) =>
            JsValue.Boolean(!getCall.Realm.GetProperty(evt, "defaultPrevented").AsBoolean);

        JsValue SetReturnValue(in JsCall setCall)
        {
            if (setCall.Length > 0 && !setCall[0].AsBoolean)
                setCall.Realm.SetProperty(evt, "defaultPrevented", JsValue.True);
            return JsValue.Undefined;
        }

        realm.DefineAccessor(evt, "returnValue", GetReturnValue, SetReturnValue);

        JsValue InitEvent(in JsCall initCall)
        {
            InitBase(evt, in initCall);
            return JsValue.Undefined;
        }

        realm.DefineValue(evt, "initEvent", realm.NewMethod("initEvent", InitEvent, 3));

        JsValue InitUIEvent(in JsCall initCall)
        {
            InitBase(evt, in initCall);
            if (initCall.Length > 3)
                initCall.Realm.SetProperty(evt, "view", initCall[3]);
            if (initCall.Length > 4)
                initCall.Realm.SetProperty(evt, "detail", initCall[4]);
            return JsValue.Undefined;
        }

        realm.DefineValue(evt, "initUIEvent", realm.NewMethod("initUIEvent", InitUIEvent, 5));

        JsValue InitCustomEvent(in JsCall initCall)
        {
            InitBase(evt, in initCall);
            // Unlike initUIEvent's `detail`, this one is written whether or not it was passed: the
            // legacy signature says a missing detail is null.
            initCall.Realm.SetProperty(evt, "detail", initCall.Length > 3 ? initCall[3] : JsValue.Null);
            return JsValue.Undefined;
        }

        realm.DefineValue(evt, "initCustomEvent", realm.NewMethod("initCustomEvent", InitCustomEvent, 4));

        JsValue InitFocusEvent(in JsCall initCall)
        {
            var initRealm = initCall.Realm;
            InitBase(evt, in initCall);
            if (initCall.Length > 3)
                initRealm.SetProperty(evt, "view", initCall[3]);
            if (initCall.Length > 4)
                initRealm.SetProperty(evt, "detail", JsValue.Number(initRealm.ToNumber(initCall[4])));
            if (initCall.Length > 5)
                initRealm.SetProperty(evt, "relatedTarget", initCall[5]);
            return JsValue.Undefined;
        }

        realm.DefineValue(evt, "initFocusEvent", realm.NewMethod("initFocusEvent", InitFocusEvent, 6));

        JsValue InitKeyboardEvent(in JsCall initCall)
        {
            var initRealm = initCall.Realm;
            InitBase(evt, in initCall);
            if (initCall.Length > 3)
                initRealm.SetProperty(evt, "view", initCall[3]);
            if (initCall.Length > 4)
                initRealm.SetProperty(evt, "key", JsValue.String(initRealm.ToJsString(initCall[4])));
            if (initCall.Length > 5)
                initRealm.SetProperty(evt, "location", JsValue.Number(initRealm.ToNumber(initCall[5])));
            if (initCall.Length > 6)
                initRealm.SetProperty(evt, "ctrlKey", JsValue.Boolean(initCall[6].AsBoolean));
            if (initCall.Length > 7)
                initRealm.SetProperty(evt, "altKey", JsValue.Boolean(initCall[7].AsBoolean));
            if (initCall.Length > 8)
                initRealm.SetProperty(evt, "shiftKey", JsValue.Boolean(initCall[8].AsBoolean));
            if (initCall.Length > 9)
                initRealm.SetProperty(evt, "metaKey", JsValue.Boolean(initCall[9].AsBoolean));
            if (initCall.Length > 10)
                initRealm.SetProperty(evt, "repeat", JsValue.Boolean(initCall[10].AsBoolean));
            if (initCall.Length > 11)
            {
                var keyCode = initRealm.ToNumber(initCall[11]);
                initRealm.SetProperty(evt, "keyCode", JsValue.Number(keyCode));
                initRealm.SetProperty(evt, "which", JsValue.Number(keyCode));
            }

            if (initCall.Length > 12)
            {
                var charCode = initRealm.ToNumber(initCall[12]);
                initRealm.SetProperty(evt, "charCode", JsValue.Number(charCode));
                if (charCode != 0)
                    initRealm.SetProperty(evt, "which", JsValue.Number(charCode));
            }

            return JsValue.Undefined;
        }

        realm.DefineValue(evt, "initKeyboardEvent", realm.NewMethod("initKeyboardEvent", InitKeyboardEvent, 13));

        JsValue InitMouseEvent(in JsCall initCall)
        {
            var initRealm = initCall.Realm;
            InitBase(evt, in initCall);
            if (initCall.Length > 3)
                initRealm.SetProperty(evt, "view", initCall[3]);
            if (initCall.Length > 4)
                initRealm.SetProperty(evt, "detail", JsValue.Number(initRealm.ToNumber(initCall[4])));
            if (initCall.Length > 5)
                initRealm.SetProperty(evt, "screenX", JsValue.Number(initRealm.ToNumber(initCall[5])));
            if (initCall.Length > 6)
                initRealm.SetProperty(evt, "screenY", JsValue.Number(initRealm.ToNumber(initCall[6])));
            if (initCall.Length > 7)
            {
                var clientX = JsValue.Number(initRealm.ToNumber(initCall[7]));
                initRealm.SetProperty(evt, "clientX", clientX);
                initRealm.SetProperty(evt, "x", clientX);
            }

            if (initCall.Length > 8)
            {
                var clientY = JsValue.Number(initRealm.ToNumber(initCall[8]));
                initRealm.SetProperty(evt, "clientY", clientY);
                initRealm.SetProperty(evt, "y", clientY);
            }

            if (initCall.Length > 9)
                initRealm.SetProperty(evt, "ctrlKey", JsValue.Boolean(initCall[9].AsBoolean));
            if (initCall.Length > 10)
                initRealm.SetProperty(evt, "altKey", JsValue.Boolean(initCall[10].AsBoolean));
            if (initCall.Length > 11)
                initRealm.SetProperty(evt, "shiftKey", JsValue.Boolean(initCall[11].AsBoolean));
            if (initCall.Length > 12)
                initRealm.SetProperty(evt, "metaKey", JsValue.Boolean(initCall[12].AsBoolean));
            if (initCall.Length > 13)
            {
                var button = initRealm.ToNumber(initCall[13]);
                initRealm.SetProperty(evt, "button", JsValue.Number(button));
                initRealm.SetProperty(evt, "buttons", JsValue.Number(button switch
                {
                    0 => 1,
                    1 => 4,
                    2 => 2,
                    _ => 0
                }));
            }

            if (initCall.Length > 14)
                initRealm.SetProperty(evt, "relatedTarget", initCall[14]);
            return JsValue.Undefined;
        }

        realm.DefineValue(evt, "initMouseEvent", realm.NewMethod("initMouseEvent", InitMouseEvent, 15));

        JsValue InitWheelEvent(in JsCall initCall)
        {
            var initRealm = initCall.Realm;
            InitBase(evt, in initCall);
            if (initCall.Length > 3)
                initRealm.SetProperty(evt, "view", initCall[3]);
            if (initCall.Length > 4)
                initRealm.SetProperty(evt, "detail", JsValue.Number(initRealm.ToNumber(initCall[4])));
            if (initCall.Length > 5)
                initRealm.SetProperty(evt, "screenX", JsValue.Number(initRealm.ToNumber(initCall[5])));
            if (initCall.Length > 6)
                initRealm.SetProperty(evt, "screenY", JsValue.Number(initRealm.ToNumber(initCall[6])));
            if (initCall.Length > 7)
            {
                var clientX = JsValue.Number(initRealm.ToNumber(initCall[7]));
                initRealm.SetProperty(evt, "clientX", clientX);
                initRealm.SetProperty(evt, "x", clientX);
            }

            if (initCall.Length > 8)
            {
                var clientY = JsValue.Number(initRealm.ToNumber(initCall[8]));
                initRealm.SetProperty(evt, "clientY", clientY);
                initRealm.SetProperty(evt, "y", clientY);
            }

            if (initCall.Length > 9)
                initRealm.SetProperty(evt, "button", JsValue.Number(initRealm.ToNumber(initCall[9])));
            if (initCall.Length > 10)
                initRealm.SetProperty(evt, "relatedTarget", initCall[10]);
            if (initCall.Length > 11)
            {
                var modifiers = initRealm.ToJsString(initCall[11])
                    .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                initRealm.SetProperty(evt, "ctrlKey", JsValue.Boolean(Array.Exists(modifiers, m => string.Equals(m, "Control", StringComparison.OrdinalIgnoreCase))));
                initRealm.SetProperty(evt, "altKey", JsValue.Boolean(Array.Exists(modifiers, m => string.Equals(m, "Alt", StringComparison.OrdinalIgnoreCase))));
                initRealm.SetProperty(evt, "shiftKey", JsValue.Boolean(Array.Exists(modifiers, m => string.Equals(m, "Shift", StringComparison.OrdinalIgnoreCase))));
                initRealm.SetProperty(evt, "metaKey", JsValue.Boolean(Array.Exists(modifiers, m => string.Equals(m, "Meta", StringComparison.OrdinalIgnoreCase))));
            }

            if (initCall.Length > 12)
                initRealm.SetProperty(evt, "deltaX", JsValue.Number(initRealm.ToNumber(initCall[12])));
            if (initCall.Length > 13)
                initRealm.SetProperty(evt, "deltaY", JsValue.Number(initRealm.ToNumber(initCall[13])));
            if (initCall.Length > 14)
                initRealm.SetProperty(evt, "deltaZ", JsValue.Number(initRealm.ToNumber(initCall[14])));
            if (initCall.Length > 15)
                initRealm.SetProperty(evt, "deltaMode", JsValue.Number(initRealm.ToNumber(initCall[15])));
            return JsValue.Undefined;
        }

        realm.DefineValue(evt, "initWheelEvent", realm.NewMethod("initWheelEvent", InitWheelEvent, 16));
        return evt;
    }

    /// <summary>
    /// The <c>type</c>/<c>bubbles</c>/<c>cancelable</c> prefix every member of the <c>init…Event</c>
    /// family begins with, each argument written only when it was supplied.
    /// </summary>
    /// <remarks>
    /// One reading rather than eight copies of it — the family shares the first three parameters by
    /// definition, and the copies could only differ by mistake. The guard is the argument count, not
    /// <c>undefined</c>: <c>initEvent("click")</c> leaves <c>bubbles</c> at its constructed value,
    /// while <c>initEvent("click", undefined)</c> writes falsity, which is what the count test these
    /// replace has always said.
    /// </remarks>
    private static void InitBase(JsValue evt, in JsCall call)
    {
        var realm = call.Realm;
        if (call.Length > 0)
            realm.SetProperty(evt, "type", JsValue.String(realm.ToJsString(call[0])));
        if (call.Length > 1)
            realm.SetProperty(evt, "bubbles", JsValue.Boolean(call[1].AsBoolean));
        if (call.Length > 2)
            realm.SetProperty(evt, "cancelable", JsValue.Boolean(call[2].AsBoolean));
    }
}
