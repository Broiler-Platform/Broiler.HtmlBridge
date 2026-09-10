using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The web-messaging feature binding module (HtmlBridge complexity-reduction roadmap Phase 3, P3.10).
/// It co-locates the whole feature: <c>window.postMessage</c>, <c>MessageChannel</c>/<c>MessagePort</c>
/// (creation, <c>postMessage</c>, <c>start</c>/<c>close</c>/<c>onmessage</c>, the port message queue),
/// structured-clone/transfer-list handling and <c>MessageEvent</c> construction. It <b>owns</b> the
/// Phase 2 <see cref="MessagePortRegistry"/> state authority (entangled peers, closed/started marks
/// and the per-port pending-message queue).
///
/// It also owns the generic <c>EventTarget</c> dispatch (<c>addEventListener</c>/
/// <c>removeEventListener</c>/<c>dispatchEvent</c> with capture/target/bubble-free propagation control)
/// that is installed on message ports <em>and</em> on sub-windows — the two non-node event targets in
/// the bridge. That dispatch is co-located here (its listeners already come from the shared
/// <see cref="EventTargetRegistry"/>) pending a dedicated generic-EventTarget/Window module; sub-window
/// installation goes through the module's <see cref="InstallEventTargetApi"/> entry point.
///
/// The module depends on the shared <see cref="EventTargetRegistry"/> (generic-target listeners +
/// owner-window map, which it does not own) and reaches the document's browsing-context operations —
/// window resolution, the window-context switch, frame-action queueing and top-window dispatch —
/// through the narrow <see cref="IMessagingHost"/> contract. It never touches an arbitrary bridge
/// field. The static, engine-neutral bridge helper <c>DomBridge.InvokeEventListener</c> is called
/// directly; a <c>DataCloneError</c> is raised through <see cref="IJsCalls.DomError"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole feature is migrated to JSEAL bar one edge, and this is which and why.</b> Everything
/// that builds or routes a message — the ports, the channel, the <c>MessageEvent</c>, the origin
/// comparison, the pending-message queue, the structured clone, the transfer list and the whole
/// <see cref="IMessagingHost"/> contract — speaks <see cref="IJsRealm"/> and names no engine type.
/// The clone is <see cref="IJsClone.Clone"/>, which is the same engine algorithm reached through the
/// realm that owns it rather than through a static; the transfer list is
/// <see cref="IJsClone.ClassifyTransferable"/> plus <c>WorkerTransfer.ArrayElements</c>, which is the
/// hole-skipping walk expressed as the own-enumerable-index question it always was. What still names
/// the engine:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>listener <em>registration</em> in the generic <c>EventTarget</c> dispatch</b> — not the dispatch
/// itself, which builds and stamps the event through the realm, and no longer the store either:
/// <see cref="EventTargetRegistry"/> keys its listener and owner maps on <see cref="JsValue"/> now,
/// so a port and a sub-window go in as handles. What is left is one record and what reads it.
/// <c>EventListenerRegistration.Listener</c> is a Broiler.JS value, declared in
/// <c>DomBridge/RuntimeStates.cs</c>; <c>EventListenerBinding</c>'s two operations are written
/// against it, and <c>DomBridge.InvokeEventListener</c> takes one. That record is outside this
/// round's files and is shared with the element, document, window and form-submit paths, so
/// <see cref="AddEventListener"/>/<see cref="RemoveEventListener"/> keep their engine argument frame:
/// a listener, and an <c>addEventListener</c> options argument, are routinely primitives —
/// <c>addEventListener(t, f, true)</c> — which the engine frame carries and a handle-to-engine
/// conversion re-materialises, losing the identity <c>removeEventListener</c> matches on. The frame
/// goes when the record does, in the same step.
/// </description></item>
/// </list>
/// </remarks>
internal sealed class MessagingBinding(IMessagingHost host, EventTargetRegistry eventTargets)
{
    private readonly IMessagingHost _host = host;
    private readonly EventTargetRegistry _eventTargets = eventTargets;

    // P2.6 state authority for MessageChannel/MessagePort (peers, closed/started marks, queued
    // messages). Owned here now that the whole messaging feature is co-located.
    private readonly MessagePortRegistry _messagePorts = new();

    /// <summary>Releases all message-channel/port state (called by the bridge's session reset).</summary>
    internal void ClearPorts() => _messagePorts.Clear();

    // ==================== Generic EventTarget dispatch ====================
    // Installed on message ports and on sub-windows (the two non-node event targets).
    //
    // THE EVENT, THE TARGET AND THE STORE ARE THE REALM'S; A REGISTRATION IS STILL THE ENGINE'S. See
    // the bullet in this class's remarks. Everything this section does to an event object — reading
    // its type, stamping target/currentTarget/eventPhase, installing stopPropagation/preventDefault/
    // composedPath and the two legacy accessors — goes through IJsRealm, and so do the listener and
    // owner-window maps it files a target in. What does not is one record: a registration's listener
    // is a Broiler.JS value (DomBridge/RuntimeStates.cs), which is what EventListenerBinding's two
    // operations and the bridge's listener invoker are written against. Neither that record nor the
    // four other firing paths that share it belong to this round.

    /// <summary>Installs <c>addEventListener</c>/<c>removeEventListener</c>/<c>dispatchEvent</c> on a
    /// generic event target (a message port or a sub-window).</summary>
    /// <remarks>
    /// <para>
    /// The target is a handle now that both callers hold one — <see cref="SubWindowBinding"/> and
    /// <see cref="CreateMessagePort"/> — and it is what the listener store is keyed on and what the
    /// three operations close over. The engine object is unwrapped once here, for the two
    /// installations that still need one; that is a cast over the object the handle already carries,
    /// so the members land on the same target either way.
    /// </para>
    /// <para>
    /// <b>The first two operations keep their engine argument frame, and it is not their own pin.</b>
    /// They hand their listener and options arguments straight to <see cref="EventListenerBinding"/>,
    /// whose two operations take engine values because <c>EventListenerRegistration.Listener</c> is
    /// one — and that record is declared in <c>DomBridge/RuntimeStates.cs</c>, outside this round.
    /// Both arguments are routinely primitives (<c>addEventListener(t, f, true)</c>), which the engine
    /// frame carries and a re-materialising conversion would not: forwarding a primitive listener
    /// through one would mint a fresh engine value per call and break the identity
    /// <c>removeEventListener</c> matches on. So this pair moves with the record, in one step, rather
    /// than acquiring that fault here. <c>dispatchEvent</c> has no such argument and is installed
    /// through the realm <em>in its original position</em>, because property order is what
    /// <c>Object.getOwnPropertyNames</c> reports.
    /// </para>
    /// </remarks>
    internal void InstallEventTargetApi(JsValue target, string logContext)
    {
        var realm = _host.Realm;

        // All three are the realm's now. The first two kept the engine's frame because the listener
        // record holds an engine value and this module had no way to reach one -- the host has that
        // seam for IEventTargetHost and now offers it here too, so the frame goes and the
        // conversion stays where the record is. The lengths are unchanged, including the 3s, which
        // are this bridge's own deviation from Web IDL's 2 and not this commit's to correct.
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
        _host.AddListener(
            GetOrCreateEventTargetListeners(target, type), call[1],
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
        _host.RemoveListener(listeners, call[1], call.Length > 2 ? call[2] : JsValue.Undefined);
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

        // The store is keyed on handles now, so the target goes in as it stands. The invoker is still
        // the engine's — see the note at the head of this section — so the event is unwrapped once,
        // which is a cast rather than a conversion; the registration's listener is never named here
        // because the work is handed over as an Action instead.
        var engineEvent = JsInterop.ToEngineObject(evt);
        if (_eventTargets.TryGetTargetListeners(target, out var listenersByType) &&
            listenersByType.TryGetValue(eventType, out var listeners))
        {
            foreach (var registration in listeners.ToList())
            {
                if (immediateStopped)
                    break;

                currentListenerPassive = registration.Passive;
                RunInOwnerWindow(target, () => DomBridge.InvokeEventListener(registration.Listener, engineEvent, logContext));
                currentListenerPassive = false;

                if (registration.Once)
                    listeners.Remove(registration);
            }
        }

        realm.SetProperty(evt, "currentTarget", JsValue.Null);
        realm.SetProperty(evt, "eventPhase", JsValue.Number(0));
        return JsValue.Boolean(!prevented);
    }

    /// <remarks>
    /// The <c>on…</c> handler is read in the engine's vocabulary on purpose. It is handed to
    /// <c>DomBridge.InvokeEventListener</c>, which takes an engine value, and a handler a page set to
    /// a primitive must still reach it: a handle cannot carry one, so a JSEAL read here would have to
    /// skip the call — which is a no-op either way, but it would also skip the listener-turn bracket
    /// that invoker opens. The three cases that return early (never installed, <c>null</c>,
    /// <c>undefined</c>) are the three the former pattern tested.
    /// </remarks>
    private void InvokeEventTargetHandler(JsValue target, string eventType, JsValue evt, string logContext)
    {
        var handler = JsInterop.ToEngineObject(target)[(KeyString)$"on{eventType}"];
        if (handler is null || handler.IsNullOrUndefined)
            return;

        var engineEvent = JsInterop.ToEngineObject(evt);
        RunInOwnerWindow(target, () => DomBridge.InvokeEventListener(handler, engineEvent, logContext));
    }

    /// <summary>
    /// Runs <paramref name="invoke"/> with the global bindings switched to the window that owns
    /// <paramref name="target"/>, or as-is when there is none.
    /// </summary>
    /// <remarks>
    /// The owner-window seam speaks handles, so "no owner" arrives as a value that is not an object
    /// rather than as a CLR null; the branch is the same one. It takes the work as an
    /// <see cref="Action"/> rather than as a listener because the listener it would otherwise take is
    /// an engine value, and this method has no other reason to name one.
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

    // ==================== window.postMessage ====================

    /// <summary>Installs <c>window.postMessage</c> on <paramref name="window"/> (top window or a
    /// sub-window).</summary>
    /// <remarks>
    /// The engine-typed parameter is an adapter, pinned twice over:
    /// <c>DomBridge/Registration/Window.cs</c> and <see cref="SubWindowBinding"/> both hand this the
    /// engine's window object, and neither is this round's to change. The handle over it is minted
    /// once here and is what the migrated operation closes over; the member itself is installed
    /// through the realm, as a <see cref="IJsValues.NewMethod"/> because the member it replaces was
    /// built non-constructable and staying so is what makes this a refactor.
    /// </remarks>
    internal void RegisterWindowMessaging(JSObject window)
    {
        var handle = JsInterop.FromEngineObject(window);
        _host.Realm.DefineValue(handle, "postMessage",
            _host.Realm.NewMethod("postMessage", (in call) => WindowPostMessage(handle, in call), 2));
    }

    private JsValue WindowPostMessage(JsValue window, in JsCall call)
    {
        var targetWindow = call.This.IsObject ? call.This : window;
        var sourceWindow = _host.ResolveCurrentWindow();
        var (targetOrigin, ports, transfer, transferredPorts) = GetPostMessageDispatchOptions(in call);
        if (!ShouldDeliverWindowMessage(targetWindow, sourceWindow, targetOrigin))
            return JsValue.Undefined;
        var payload = CloneForMessaging(call.Length > 0 ? call[0] : JsValue.Undefined, transfer);
        CommitTransferredPorts(transferredPorts, targetWindow);
        var origin = GetWindowOrigin(sourceWindow);
        _host.QueueFrameAction(() =>
        {
            var evt = CreateMessageEvent(payload, sourceWindow, origin, ports);

            // Handle equality is reference equality for an object, which is the question
            // ReferenceEquals was asking of the two window objects.
            if (targetWindow == _host.WindowObject)
            {
                _host.DispatchWindowEvent(evt);
            }
            else
            {
                _host.RunWithWindowContext(targetWindow, () =>
                    DispatchEventTarget(targetWindow, evt, "DomBridge.window.postMessage"));
            }
        });
        return JsValue.Undefined;
    }

    /// <remarks>
    /// <para>
    /// The two-argument and the options-object spellings of <c>postMessage</c>, told apart exactly as
    /// before: an object carrying either <c>targetOrigin</c> or <c>transfer</c> is options, and
    /// anything else is a target origin to coerce. A third argument is a transfer list whichever
    /// spelling was used, and wins.
    /// </para>
    /// <para>
    /// The properties are read the same number of times, in the same order, including the
    /// short-circuit that skips reading <c>transfer</c> when <c>targetOrigin</c> is present — a page
    /// may have installed getters, and how many times one runs is as observable as what it answers.
    /// A property that was never installed reads back as <see cref="JsValue.Missing"/>, which is the
    /// CLR <see langword="null"/> the former <c>is { }</c> tests were asking about, and
    /// <see cref="JsValue.IsNullish"/> is the former <c>!= null &amp;&amp; !IsNullOrUndefined</c>.
    /// <c>ToJsString</c> rather than the handle's own rendering, because these two reads were
    /// <c>ToString()</c> on this engine and that is the observable ECMAScript coercion.
    /// </para>
    /// </remarks>
    private (string TargetOrigin, JsValue Ports, JsValue[] Transfer, List<JsValue> TransferredPorts) GetPostMessageDispatchOptions(in JsCall call)
    {
        var realm = call.Realm;
        var targetOrigin = "*";
        var transferValue = JsValue.Undefined;

        if (call.Length > 1)
        {
            var argument = call[1];
            if (argument.IsObject &&
                (!realm.GetProperty(argument, "targetOrigin").IsMissing ||
                 !realm.GetProperty(argument, "transfer").IsMissing))
            {
                var targetOriginValue = realm.GetProperty(argument, "targetOrigin");
                if (!targetOriginValue.IsNullish)
                    targetOrigin = realm.ToJsString(targetOriginValue);

                var transferProperty = realm.GetProperty(argument, "transfer");
                transferValue = transferProperty.IsMissing ? JsValue.Undefined : transferProperty;
            }
            else
            {
                targetOrigin = realm.ToJsString(argument);
            }
        }

        if (call.Length > 2)
            transferValue = call[2];

        var (ports, transfer, transferredPorts) = ExtractTransferList(transferValue);
        return (targetOrigin, ports, transfer, transferredPorts);
    }

    /// <summary>
    /// Validates a transfer list and splits it into the ports the <c>MessageEvent</c> carries, the
    /// transferable objects the clone detaches, and the ports whose owner window the send re-homes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The walk is the host's and only the entries it does not recognise are the engine's.</b> A
    /// <c>MessagePort</c> is transferable and is a thing this module owns — its identity is a peer
    /// entry in <see cref="MessagePortRegistry"/>, which no engine knows about — so ports are
    /// classified here and everything else is handed to
    /// <see cref="IJsClone.ClassifyTransferable"/>, which answers in the specification's vocabulary
    /// (may this appear in a transfer list; has it already been spent) rather than by naming a type.
    /// </para>
    /// <para>
    /// <c>WorkerTransfer.ArrayElements</c> is the former <c>GetArrayElements(withHoles: false)</c>: a
    /// hole must be skipped, because a length-and-index walk would hand it on as a non-transferable
    /// value and turn <c>postMessage(m, [ , buf])</c> into a <c>DataCloneError</c> a browser does not
    /// raise.
    /// </para>
    /// <para>
    /// Each failure <see langword="throw"/>s <see cref="IJsCalls.DomError"/> where it used to call the
    /// bridge's <c>ThrowDOMException</c>, which threw the same <c>DOMException</c> — the difference is
    /// that the compiler can now see that the path ends, so the unreachable returns are gone.
    /// </para>
    /// </remarks>
    private (JsValue Ports, JsValue[] Transfer, List<JsValue> TransferredPorts) ExtractTransferList(JsValue transferValue)
    {
        var realm = _host.Realm;

        if (transferValue.IsNullish)
            return (realm.NewArray(), [], []);

        if (!transferValue.IsArray)
            throw realm.DomError("DataCloneError", "The transfer list contains a non-transferable value.");

        var transferredPorts = new List<JsValue>();
        var seenPorts = new HashSet<JsValue>();
        var transferredBuffers = new List<JsValue>();
        var seenBuffers = new HashSet<JsValue>();

        foreach (var item in WorkerTransfer.ArrayElements(realm, transferValue))
        {
            if (item.IsObject && _messagePorts.HasPeer(item))
            {
                if (!seenPorts.Add(item))
                    throw realm.DomError("DataCloneError", "The transfer list contains duplicate transferable values.");

                transferredPorts.Add(item);
                continue;
            }

            switch (realm.ClassifyTransferable(item))
            {
                case JsTransferKind.Detached:
                    throw realm.DomError("DataCloneError", "The transfer list contains a detached ArrayBuffer.");

                case JsTransferKind.Transferable when !seenBuffers.Add(item):
                    throw realm.DomError("DataCloneError", "The transfer list contains duplicate transferable values.");

                case JsTransferKind.Transferable:
                    transferredBuffers.Add(item);
                    continue;

                default:
                    throw realm.DomError("DataCloneError", "The transfer list contains a non-transferable value.");
            }
        }

        return (realm.NewArray(transferredPorts.ToArray()), [.. transferredBuffers], transferredPorts);
    }

    private void CommitTransferredPorts(IEnumerable<JsValue> transferredPorts, JsValue targetWindow)
    {
        foreach (var port in transferredPorts)
            _eventTargets.SetOwnerWindow(port, targetWindow);
    }

    private bool ShouldDeliverWindowMessage(JsValue targetWindow, JsValue sourceWindow, string targetOrigin)
    {
        if (string.IsNullOrWhiteSpace(targetOrigin) || targetOrigin == "*")
            return true;

        if (targetOrigin == "/")
            targetOrigin = GetWindowOrigin(sourceWindow);

        return string.Equals(targetOrigin, GetWindowOrigin(targetWindow), StringComparison.Ordinal);
    }

    private string GetWindowOrigin(JsValue window)
    {
        if (!window.IsObject)
            return string.Empty;

        var realm = _host.Realm;

        // The top window's origin is the document's, taken from the host rather than read back out
        // of `location`. The window IS the global object, and RunWithWindowContext swaps `location`
        // (and `document`/`self`/`parent`) to a frame's for the duration of that frame's scripts —
        // which is precisely when a frame calls parent.postMessage. Reading the property there would
        // see the frame's own about:srcdoc and report the parent as having no origin. Taking the
        // fact instead of the mutable view is also what terminates the walk below: a top-level
        // window is its own `parent`, so an about:blank page has no further parent to inherit from.
        if (window == _host.WindowObject)
            return _host.PageOrigin;

        var location = realm.GetProperty(window, "location");
        if (location.IsObject)
        {
            var href = OwnText(realm, location, "href");
            if (string.Equals(href, "about:srcdoc", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(href, "about:blank", StringComparison.OrdinalIgnoreCase))
            {
                // An about:blank / about:srcdoc document has no origin of its own and inherits its
                // parent's. A window that parents itself is the top of the tree and has nothing left
                // to inherit from, so it ends the walk rather than recurring forever.
                var parent = realm.GetProperty(window, "parent");
                return !parent.IsObject || parent == window
                    ? string.Empty
                    : GetWindowOrigin(parent);
            }

            var origin = OwnText(realm, location, "origin");
            if (!string.IsNullOrWhiteSpace(origin))
                return origin;

            if (Uri.TryCreate(href, UriKind.Absolute, out var hrefUri))
                return Scripting.Origin.Of(hrefUri);
        }

        return string.Empty;
    }

    /// <summary>
    /// A <c>location</c> member as text, where an absent property reads as the empty string.
    /// </summary>
    /// <remarks>
    /// <c>ToJsString</c> rather than the handle's own rendering: these two reads were
    /// <c>location.href.ToString()</c>, which on this engine is the observable ECMAScript coercion
    /// and runs whatever <c>toString</c> a script assigned to a frame's <c>location</c>. Only a
    /// <em>missing</em> property short-circuits to the empty string, which is what the former
    /// <c>?.ToString() ?? string.Empty</c> did — a property that is present and <c>null</c> still
    /// coerces, to "null".
    /// </remarks>
    private static string OwnText(IJsRealm realm, JsValue target, string name)
    {
        var value = realm.GetProperty(target, name);
        return value.IsMissing ? string.Empty : realm.ToJsString(value);
    }

    /// <summary>
    /// Structured-clones a message payload, raising <c>DataCloneError</c> for a value that cannot be
    /// cloned.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One clone, in one realm, because same-document messaging has one realm: the copy this produces
    /// is the copy delivered. (A Worker message is cloned twice instead —
    /// <see cref="IJsClone.Detach"/> then <see cref="IJsClone.Adopt"/> — because the second realm is
    /// on another thread; see <see cref="WorkerBinding"/>.)
    /// </para>
    /// <para>
    /// Cloning here rather than at delivery is what the messaging model requires: the sender's
    /// <c>postMessage</c> is where a <c>DataCloneError</c> belongs, where the transfer list detaches
    /// the sender's buffers, and after which mutating the payload is invisible to the receiver.
    /// </para>
    /// </remarks>
    private JsValue CloneForMessaging(JsValue value, JsValue[] transfer)
    {
        try
        {
            return _host.Realm.Clone(value, transfer);
        }
        catch (JsEngineException)
        {
            throw _host.Realm.DomError("DataCloneError", "The object could not be cloned.");
        }
    }

    /// <summary>
    /// Builds the <c>MessageEvent</c> delivered to a window or to a port.
    /// </summary>
    /// <remarks>
    /// Every member is installed through the realm, in the order it always was, because property
    /// order is what <c>Object.keys</c> and a <c>for…in</c> over the event report. <c>data</c> is the
    /// structured clone, which is now a handle like everything else.
    /// </remarks>
    private JsValue CreateMessageEvent(JsValue data, JsValue sourceWindow, string origin, JsValue ports)
    {
        var realm = _host.Realm;
        var evt = realm.NewObject();
        realm.DefineValue(evt, "type", JsValue.String("message"));
        realm.DefineValue(evt, "bubbles", JsValue.False);
        realm.DefineValue(evt, "cancelable", JsValue.False);
        realm.DefineValue(evt, "defaultPrevented", JsValue.False);
        realm.DefineValue(evt, "data", data);
        realm.DefineValue(evt, "origin", JsValue.String(origin));
        realm.DefineValue(evt, "lastEventId", JsValue.String(string.Empty));
        realm.DefineValue(evt, "source", sourceWindow.IsObject ? sourceWindow : JsValue.Null);
        realm.DefineValue(evt, "ports", ports);
        return evt;
    }

    // ==================== MessageChannel / MessagePort ====================

    /// <summary>
    /// A <c>MessageChannel</c> as the engine object the <c>MessageChannel</c> interface object in
    /// <c>DomBridge/Registration/Registration.cs</c> hands back to a page.
    /// </summary>
    /// <remarks>
    /// An adapter, not a second implementation: that registration is not this round's to change, so
    /// the channel is built through the realm by <see cref="CreateChannel"/> and crosses here as the
    /// engine's own object, which is what the handle already holds.
    /// </remarks>
    internal JSObject CreateMessageChannel() => JsInterop.ToEngineObject(CreateChannel());

    /// <summary>Constructs a <c>MessageChannel</c> with two entangled ports.</summary>
    internal JsValue CreateChannel()
    {
        var realm = _host.Realm;
        var ownerWindow = _host.ResolveCurrentWindow();
        var port1 = CreateMessagePort(ownerWindow);
        var port2 = CreateMessagePort(ownerWindow);
        _messagePorts.Link(port1, port2);

        var channel = realm.NewObject();
        realm.DefineValue(channel, "port1", port1);
        realm.DefineValue(channel, "port2", port2);
        return channel;
    }

    private JsValue CreateMessagePort(JsValue ownerWindow)
    {
        var realm = _host.Realm;
        var port = realm.NewObject();
        var effectiveOwner = FirstObject(ownerWindow, _host.WindowObject, _host.ResolveCurrentWindow(), port);
        _eventTargets.SetOwnerWindow(port, effectiveOwner);
        InstallEventTargetApi(port, "DomBridge.messagePort.dispatchEvent");
        JsValue onMessageHandler = JsValue.Null;

        // A NewMethod because the member was built non-constructable, and in this position because
        // the member order a page enumerates is the one it always was.
        realm.DefineValue(port, "postMessage",
            realm.NewMethod("postMessage", (in call) => PortPostMessage(port, in call), 1));

        realm.DefineAccessor(port, "onmessage",
            (in _) => onMessageHandler,
            (in call) => SetOnMessage(ref onMessageHandler, port, in call));

        realm.DefineValue(port, "start", realm.NewMethod("start", (in call) => StartPort(port, in call)));

        realm.DefineValue(port, "close", realm.NewMethod("close", (in call) => ClosePort(port, in call)));

        return port;
    }

    /// <summary>The first of <paramref name="candidates"/> that is an object.</summary>
    /// <remarks>
    /// The former <c>a ?? b ?? c ?? port</c> chains, asked of handles: "is an object" is the question
    /// each <c>??</c> was asking, now that a window which does not exist crosses the host seam as
    /// JavaScript <c>null</c> rather than as a CLR one. Every chain it replaces ended in a value that
    /// is always an object, so the final fallback is unreachable.
    /// </remarks>
    private static JsValue FirstObject(params ReadOnlySpan<JsValue> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.IsObject)
                return candidate;
        }

        return JsValue.Undefined;
    }

    /// <remarks>
    /// <c>this</c> is the port the call was made on when there is one, and the port the member was
    /// installed on otherwise — the same two cases the former engine-typed receiver test drew,
    /// asked of a handle. The <c>transfer</c> property is read twice and in the same order as before,
    /// because a page may have installed a getter for it.
    /// </remarks>
    private JsValue PortPostMessage(JsValue port, in JsCall call)
    {
        var realm = call.Realm;
        var sourcePort = call.This.IsObject ? call.This : port;
        if (_messagePorts.IsClosed(sourcePort) || !_messagePorts.TryGetPeer(sourcePort, out var targetPort) || _messagePorts.IsClosed(targetPort))
        {
            return JsValue.Undefined;
        }

        var transferValue = JsValue.Undefined;
        if (call.Length > 1)
        {
            var argument = call[1];
            if (argument.IsObject && !realm.GetProperty(argument, "transfer").IsMissing)
            {
                var transferProperty = realm.GetProperty(argument, "transfer");
                transferValue = transferProperty.IsMissing ? JsValue.Undefined : transferProperty;
            }
            else
            {
                transferValue = argument;
            }
        }

        var targetOwner = FirstObject(_host.ResolveOwnerWindow(targetPort), _host.WindowObject, sourcePort);
        var (ports, transfer, transferredPorts) = ExtractTransferList(transferValue);
        var payload = CloneForMessaging(call.Length > 0 ? call[0] : JsValue.Undefined, transfer);
        CommitTransferredPorts(transferredPorts, targetOwner);
        _host.QueueFrameAction(() =>
        {
            if (_messagePorts.IsClosed(sourcePort) || _messagePorts.IsClosed(targetPort))
            {
                return;
            }

            var evt = CreateMessageEvent(payload, JsValue.Null, string.Empty, ports);
            DispatchOrQueueMessagePortEvent(targetPort, evt);
        });
        return JsValue.Undefined;
    }

    private JsValue SetOnMessage(ref JsValue onMessageHandler, JsValue port, in JsCall call)
    {
        onMessageHandler = call.Length > 0 ? call[0] : JsValue.Undefined;
        if (!onMessageHandler.IsNullish)
        {
            ActivateMessagePort(call.This.IsObject ? call.This : port);
        }

        return JsValue.Undefined;
    }

    private JsValue StartPort(JsValue port, in JsCall call)
    {
        ActivateMessagePort(call.This.IsObject ? call.This : port);
        return JsValue.Undefined;
    }

    private JsValue ClosePort(JsValue port, in JsCall call)
    {
        var currentPort = call.This.IsObject ? call.This : port;
        _messagePorts.Close(currentPort);
        return JsValue.Undefined;
    }

    private void DispatchOrQueueMessagePortEvent(JsValue targetPort, JsValue evt)
    {
        if (_messagePorts.IsClosed(targetPort))
            return;

        if (CanDispatchMessagePortEvent(targetPort))
        {
            DispatchMessagePortEvent(targetPort, evt);
            return;
        }

        _messagePorts.Enqueue(targetPort, evt);
    }

    private bool CanDispatchMessagePortEvent(JsValue targetPort)
        => _messagePorts.IsStarted(targetPort) || HasOnMessageHandler(targetPort);

    /// <remarks>
    /// A property that was never installed reads back as <see cref="JsValue.Missing"/> here, which
    /// <see cref="JsValue.IsNullish"/> covers along with <c>null</c> and <c>undefined</c> — the same
    /// three cases the former <c>is { } handler &amp;&amp; !handler.IsNullOrUndefined</c> tested.
    /// </remarks>
    private bool HasOnMessageHandler(JsValue targetPort)
        => !_host.Realm.GetProperty(targetPort, "onmessage").IsNullish;

    private void ActivateMessagePort(JsValue port)
    {
        if (_messagePorts.IsClosed(port))
            return;

        _messagePorts.Start(port);

        var queuedEvents = _messagePorts.TakeQueued(port);
        if (queuedEvents is null)
            return;

        foreach (var evt in queuedEvents)
        {
            DispatchMessagePortEvent(port, evt);
        }
    }

    /// <remarks>
    /// The owner-window branch this used to spell out twice is <see cref="RunInOwnerWindow"/>, which
    /// is the same two-case decision the listener paths make.
    /// </remarks>
    private void DispatchMessagePortEvent(JsValue targetPort, JsValue evt) =>
        RunInOwnerWindow(targetPort, () =>
            DispatchEventTarget(targetPort, evt, "DomBridge.messagePort.postMessage"));
}
