using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The generic <c>EventTarget</c> dispatch <see cref="MessagingBinding"/> installs on message ports and
/// sub-windows; see the class remarks for why it lives in this module.
/// </summary>
internal sealed partial class MessagingBinding
{
    // ==================== Generic EventTarget dispatch ====================
    // Installed on message ports and on sub-windows (the two non-node event targets).
    //
    // THE EVENT, THE TARGET, THE STORE AND THE REGISTRATION ARE ALL THE REALM'S. This note used to say
    // a registration was still the engine's; its listener is a JsValue now, so everything below --
    // reading the event's type, stamping target/currentTarget/eventPhase, installing the propagation
    // operations, filing a target in the listener and owner-window maps, adding and removing a
    // listener, and firing one -- goes through IJsRealm.

    /// <summary>Installs <c>addEventListener</c>/<c>removeEventListener</c>/<c>dispatchEvent</c> on a
    /// generic event target (a message port or a sub-window).</summary>
    /// <remarks>
    /// <para>
    /// The target is a handle, which both callers hold -- <see cref="SubWindowBinding"/> and
    /// <see cref="CreateMessagePort"/> -- and it is what the listener store is keyed on and what the
    /// three operations close over.
    /// </para>
    /// <para>
    /// <b>This remark said the first two operations kept an engine argument frame, and that an engine
    /// object was unwrapped here for them.</b> Neither was so: all three are minted below through the
    /// realm with a JSEAL frame, and nothing here unwraps anything. What the first two did depend on was
    /// the listener record, whose engine-typed field made the host convert every listener and
    /// <c>options</c> argument on the way in. The record holds a handle now and the two call
    /// <see cref="EventListenerBinding"/> directly. <c>dispatchEvent</c> is installed <em>in its original
    /// position</em>, because property order is what <c>Object.getOwnPropertyNames</c> reports.
    /// </para>
    /// </remarks>
    internal void InstallEventTargetApi(JsValue target, string logContext)
    {
        var realm = _host.Realm;

        // All three are the realm's. The lengths are unchanged, including the 3s, which are this
        // bridge's own deviation from Web IDL's 2, recorded rather than corrected here.
        realm.DefineValue(target, "addEventListener",
            realm.NewMethod("addEventListener", (in call) => AddEventListener(target, in call), 3));

        realm.DefineValue(target, "removeEventListener",
            realm.NewMethod("removeEventListener", (in call) => RemoveEventListener(target, in call), 3));

        realm.DefineValue(target, "dispatchEvent",
            realm.NewMethod("dispatchEvent", (in call) => DispatchEvent(target, logContext, in call), 1));
    }

    private JsValue AddEventListener(JsValue target, in JsCall call)
    {
        if (call.Length < 2)
            return JsValue.Undefined;
        var type = call.Realm.ToJsString(call[0]);
        EventListenerBinding.AddListener(
            call.Realm, GetOrCreateEventTargetListeners(target, type), call[1],
            call.Length > 2 ? call[2] : JsValue.Undefined);
        return JsValue.Undefined;
    }

    private JsValue RemoveEventListener(JsValue target, in JsCall call)
    {
        if (call.Length < 2)
            return JsValue.Undefined;
        var type = call.Realm.ToJsString(call[0]);
        var listeners = _eventTargets.TryGetTargetListeners(target, out var listenersByType) &&
                        listenersByType.TryGetValue(type, out var byType)
            ? byType
            : null;
        EventListenerBinding.RemoveListener(call.Realm, listeners, call[1], call.Length > 2 ? call[2] : JsValue.Undefined);
        return JsValue.Undefined;
    }

    /// <remarks>
    /// <c>!IsObject</c> is the question the former engine-typed pattern match was asking, and it
    /// covers the no-argument case the same way — a missing argument is not an object.
    /// </remarks>
    private JsValue DispatchEvent(JsValue target, string logContext, in JsCall call)
    {
        if (call.Length == 0 || !call[0].IsObject)
            return JsValue.True;
        return DispatchEventTarget(target, call[0], logContext);
    }

    private List<EventListenerRegistration> GetOrCreateEventTargetListeners(JsValue target, string type)
    {
        var listenersByType = _eventTargets.TargetListenersForAdd(target);

        if (!listenersByType.TryGetValue(type, out var listeners))
        {
            listeners = [];
            listenersByType[type] = listeners;
        }

        return listeners;
    }

    /// <remarks>
    /// <para>
    /// The member <em>order</em> is the order it always was, and it is observable: a page that
    /// enumerates a dispatched event sees <c>target</c>, <c>currentTarget</c>, <c>eventPhase</c>, the
    /// three propagation operations, the two legacy accessors and <c>composedPath</c> in exactly this
    /// sequence. <c>srcElement</c> is a plain write rather than a definition because it always was —
    /// it is the only member here that goes through the object's own setter path.
    /// </para>
    /// <para>
    /// <c>ToJsString</c> rather than the handle's own rendering for the event type: that read was
    /// <c>evt["type"].ToString()</c>, which on this engine is the observable ECMAScript coercion and
    /// may run a <c>toString</c> the page wrote. Only a property that was never installed
    /// short-circuits, which is what the former <c>?.ToString() ?? "unknown"</c> did.
    /// </para>
    /// </remarks>
    private JsValue DispatchEventTarget(JsValue target, JsValue evt, string logContext)
    {
        var realm = _host.Realm;

        var typeValue = realm.GetProperty(evt, "type");
        var eventType = typeValue.IsMissing ? "unknown" : realm.ToJsString(typeValue);

        realm.DefineValue(evt, "target", target);
        realm.SetProperty(evt, "srcElement", target);
        realm.DefineValue(evt, "currentTarget", target);
        realm.DefineValue(evt, "eventPhase", JsValue.Number(2));

        var immediateStopped = false;

        // AsBoolean, not the realm's coercion: ECMAScript ToBoolean never runs script, so this is the
        // whole of what the former BooleanValue did — and a property that was never installed reads
        // back as Missing, which is falsy, exactly as the former engine-typed pattern failing was.
        var prevented = realm.GetProperty(evt, "defaultPrevented").AsBoolean;
        var currentListenerPassive = false;
        var legacyCancelBubble = false;
        realm.SetProperty(evt, "defaultPrevented", JsValue.Boolean(prevented));

        realm.DefineValue(evt, "stopPropagation",
            realm.NewMethod("stopPropagation", (in _) => StopPropagation(ref legacyCancelBubble, in _)));

        realm.DefineValue(evt, "stopImmediatePropagation",
            realm.NewMethod("stopImmediatePropagation",
                (in _) => StopImmediatePropagation(ref immediateStopped, ref legacyCancelBubble, in _)));

        realm.DefineValue(evt, "preventDefault",
            realm.NewMethod("preventDefault", (in _) => PreventDefault(currentListenerPassive, evt, ref prevented, in _)));

        realm.DefineAccessor(evt, "cancelBubble",
            (in _) => JsValue.Boolean(legacyCancelBubble),
            (in setArgs) => SetCancelBubble(ref legacyCancelBubble, in setArgs));

        realm.DefineAccessor(evt, "returnValue",
            (in _) => JsValue.Boolean(!prevented),
            (in setArgs) => SetReturnValue(currentListenerPassive, evt, ref prevented, in setArgs));

        realm.DefineValue(evt, "composedPath",
            realm.NewMethod("composedPath", (in _) => realm.NewArray([target])));

        InvokeEventTargetHandler(target, eventType, evt, logContext);

        // The store is keyed on handles, and the listener, the event and the invoker's parameters are
        // all handles too, so nothing in this loop converts. Each listener is fired inside an Action
        // because RunInOwnerWindow takes one; see its remarks.
        if (_eventTargets.TryGetTargetListeners(target, out var listenersByType) &&
            listenersByType.TryGetValue(eventType, out var listeners))
        {
            EventListenerBinding.InvokeListeners(listeners,
                listener => RunInOwnerWindow(target, () => DomBridgeUtils.InvokeEventListener(realm, listener, evt, logContext)),
                ref immediateStopped, ref currentListenerPassive);
        }

        realm.SetProperty(evt, "currentTarget", JsValue.Null);
        realm.SetProperty(evt, "eventPhase", JsValue.Number(0));
        return JsValue.Boolean(!prevented);
    }

    /// <remarks>
    /// <para>
    /// The <c>on…</c> handler is read through the realm, which is the same engine indexer over the same
    /// object, so a getter a page installed for it still runs. The three cases that return early are the
    /// three the engine-typed test named: a property that was never installed reads back as
    /// <see cref="JsValue.Missing"/> where the indexer answered a CLR <see langword="null"/>, and
    /// <see cref="JsValue.IsNullish"/> is that, <c>null</c> and <c>undefined</c>.
    /// </para>
    /// <para>
    /// A handler a page set to a primitive still reaches the invoker, as it always did -- a handle
    /// carries a string, a number or a boolean by value -- and the invoker still calls nothing for it,
    /// because it is neither callable nor an object with a <c>handleEvent</c>. So all a primitive handler
    /// causes is the listener-turn bracket that invoker opens, as before.
    /// </para>
    /// </remarks>
    private void InvokeEventTargetHandler(JsValue target, string eventType, JsValue evt, string logContext)
    {
        var realm = _host.Realm;
        var handler = realm.GetProperty(target, $"on{eventType}");
        if (handler.IsNullish)
            return;

        RunInOwnerWindow(target, () => DomBridgeUtils.InvokeEventListener(realm, handler, evt, logContext));
    }

    /// <summary>
    /// Runs <paramref name="invoke"/> with the global bindings switched to the window that owns
    /// <paramref name="target"/>, or as-is when there is none.
    /// </summary>
    /// <remarks>
    /// The owner-window seam speaks handles, so "no owner" arrives as a value that is not an object
    /// rather than as a CLR null; the branch is the same one. It takes the work as an
    /// <see cref="Action"/> because its callers hand it different work: a listener or an <c>on…</c>
    /// handler to fire, and a whole dispatch from <see cref="DispatchMessagePortEvent"/>. This remark used
    /// to give the reason as the listener being an engine value; the listener is a handle now, and the
    /// Action was never optional, because that second caller has no listener to pass.
    /// </remarks>
    private void RunInOwnerWindow(JsValue target, Action invoke)
    {
        var ownerWindow = _host.ResolveOwnerWindow(target);
        if (!ownerWindow.IsObject)
        {
            invoke();
            return;
        }

        _host.RunWithWindowContext(ownerWindow, invoke);
    }

    private static JsValue StopPropagation(ref bool legacyCancelBubble, in JsCall _)
    {
        legacyCancelBubble = true;
        return JsValue.Undefined;
    }

    private static JsValue StopImmediatePropagation(ref bool immediateStopped, ref bool legacyCancelBubble, in JsCall _)
    {
        immediateStopped = true;
        legacyCancelBubble = true;
        return JsValue.Undefined;
    }

    /// <remarks>
    /// <c>cancelable</c> is read before the guard, as it always was, so a getter the page installed
    /// for it runs whether or not the event turns out to be cancelable. A never-installed
    /// <c>cancelable</c> reads back as <see cref="JsValue.Missing"/>, whose <c>AsBoolean</c> is
    /// <see langword="false"/> — the same answer the former CLR-null check gave.
    /// </remarks>
    private static JsValue PreventDefault(bool currentListenerPassive, JsValue evt, ref bool prevented, in JsCall call)
    {
        var realm = call.Realm;
        var cancelable = realm.GetProperty(evt, "cancelable");
        if (!currentListenerPassive && cancelable.AsBoolean)
        {
            prevented = true;
            realm.SetProperty(evt, "defaultPrevented", JsValue.True);
        }

        return JsValue.Undefined;
    }

    private static JsValue SetCancelBubble(ref bool legacyCancelBubble, in JsCall setArgs)
    {
        if (setArgs.Length > 0 && setArgs[0].AsBoolean)
            legacyCancelBubble = true;
        return JsValue.Undefined;
    }

    /// <inheritdoc cref="PreventDefault"/>
    private static JsValue SetReturnValue(bool currentListenerPassive, JsValue evt, ref bool prevented, in JsCall setArgs)
    {
        var realm = setArgs.Realm;
        var cancelable = realm.GetProperty(evt, "cancelable");
        if (setArgs.Length > 0 && !setArgs[0].AsBoolean && !currentListenerPassive && cancelable.AsBoolean)
        {
            prevented = true;
            realm.SetProperty(evt, "defaultPrevented", JsValue.True);
        }

        return JsValue.Undefined;
    }
}
