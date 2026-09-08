using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Array.Typed;
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
/// <b>The channel/port object model is migrated to JSEAL; three edges are not, and this is which and
/// why.</b> Everything that builds or routes a message — the ports, the channel, the
/// <c>MessageEvent</c>, the origin comparison, the pending-message queue and the whole
/// <see cref="IMessagingHost"/> contract — speaks <see cref="IJsRealm"/> and names no engine type.
/// What still does:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>the structured clone at the centre of both <c>postMessage</c> bodies.</b> JSEAL declares no
/// structured-clone operation, so cloning is <c>JSGlobalStatic.StructuredClone</c> as it always was.
/// That is not a formatting choice: a cloned payload may be a primitive, and a JSEAL handle over a
/// primitive carries no engine instance (see <see cref="JsInterop"/>), so the value cannot cross the
/// seam in either direction. The two callbacks therefore keep their engine argument frame, because
/// that frame is where the payload is read.
/// </description></item>
/// <item><description>
/// <b>the transfer list.</b> Deciding that an entry is a transferable <c>ArrayBuffer</c>, that it is
/// not already detached, and building the <c>{ transfer: [...] }</c> options the engine's
/// <c>structuredClone</c> understands are all statements about a type JSEAL does not model. The walk
/// over the list is part of the same edge: <c>GetArrayElements(withHoles: false)</c> skips a hole,
/// where a length-and-index walk would hand the hole on as a non-transferable value and turn it into
/// a <c>DataCloneError</c> that a browser does not raise.
/// </description></item>
/// <item><description>
/// <b>listener <em>registration</em> in the generic <c>EventTarget</c> dispatch</b> — not the dispatch
/// itself, which now builds and stamps the event through the realm. What is left is the stores:
/// <see cref="EventTargetRegistry"/> keys its listener and owner maps on the engine's objects,
/// <c>EventListenerRegistration.Listener</c> and <c>EventListenerBinding</c>'s two operations take
/// engine values, and <c>DomBridge.InvokeEventListener</c> takes one. A listener, and an
/// <c>addEventListener</c> options argument, are routinely primitives — <c>addEventListener(t, f,
/// true)</c> — so this edge has the same handle-carries-no-primitive problem as the clone. It moves
/// when those four do, rather than one call deeper.
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
    // THE EVENT IS THE REALM'S; THE LISTENER STORE IS STILL THE ENGINE'S. See the third bullet in
    // this class's remarks. Everything this section does to an event object — reading its type,
    // stamping target/currentTarget/eventPhase, installing stopPropagation/preventDefault/
    // composedPath and the two legacy accessors — goes through IJsRealm. Registration does not:
    // the listener map, the owner-window map, a registration's listener and the bridge's listener
    // invoker are all declared in the engine's vocabulary, and the values they carry include
    // primitives that a JSEAL handle cannot hold. That half migrates when those four files do.

    /// <summary>Installs <c>addEventListener</c>/<c>removeEventListener</c>/<c>dispatchEvent</c> on a
    /// generic event target (a message port or a sub-window).</summary>
    /// <remarks>
    /// The engine-typed parameter is an adapter pinned by <see cref="SubWindowBinding"/> and by
    /// <see cref="CreateMessagePort"/>'s own engine-typed installation; the handle over it is minted
    /// once here and is what the migrated operation closes over. The first two operations keep their
    /// engine argument frame because their listener and options arguments may be primitives; the
    /// third does not, and is installed through the realm <em>in its original position</em>, because
    /// property order is what <c>Object.getOwnPropertyNames</c> reports.
    /// </remarks>
    internal void InstallEventTargetApi(JSObject target, string logContext)
    {
        var handle = JsInterop.FromEngineObject(target);
        var realm = _host.Realm;

        target.FastAddValue("addEventListener",
            new DomFunction((in a) => AddEventListener(target, in a), "addEventListener", 3),
            JSPropertyAttributes.EnumerableConfigurableValue);

        target.FastAddValue("removeEventListener",
            new DomFunction((in a) => RemoveEventListener(target, in a), "removeEventListener", 3),
            JSPropertyAttributes.EnumerableConfigurableValue);

        realm.DefineValue(handle, "dispatchEvent",
            realm.NewMethod("dispatchEvent", (in call) => DispatchEvent(handle, logContext, in call), 1));
    }

    private JSValue AddEventListener(JSObject target, in Arguments a)
    {
        if (a.Length < 2)
            return JSUndefined.Value;
        var type = a[0].ToString();
        EventListenerBinding.AddListener(
            GetOrCreateEventTargetListeners(target, type), a[1], a.Length > 2 ? a[2] : JSUndefined.Value);
        return JSUndefined.Value;
    }

    private JSValue RemoveEventListener(JSObject target, in Arguments a)
    {
        if (a.Length < 2)
            return JSUndefined.Value;
        var type = a[0].ToString();
        var listeners = _eventTargets.TryGetTargetListeners(target, out var listenersByType) &&
                        listenersByType.TryGetValue(type, out var byType)
            ? byType
            : null;
        EventListenerBinding.RemoveListener(listeners, a[1], a.Length > 2 ? a[2] : JSUndefined.Value);
        return JSUndefined.Value;
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

    private List<EventListenerRegistration> GetOrCreateEventTargetListeners(JSObject target, string type)
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

        // The listener store and the invoker are the engine's — see the note at the head of this
        // section. A handle carries the engine's own object, so unwrapping it is a cast rather than a
        // conversion, and the registration's listener is never named here because the work is handed
        // over as an Action instead.
        var engineEvent = JsInterop.ToEngineObject(evt);
        if (_eventTargets.TryGetTargetListeners(JsInterop.ToEngineObject(target), out var listenersByType) &&
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
    /// Engine-typed, and pinned there twice over: <c>DomBridge/Registration/Window.cs</c> and
    /// <see cref="SubWindowBinding"/> both hand it the engine's window object, and the operation it
    /// installs reads its payload as an engine value because the structured clone at that payload's
    /// centre has no JSEAL expression.
    /// </remarks>
    internal void RegisterWindowMessaging(JSObject window)
    {
        window.FastAddValue(
            "postMessage",
            new DomFunction((in a) => WindowPostMessage(window, in a), "postMessage", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);
    }

    private JSValue WindowPostMessage(JSObject window, in Arguments a)
    {
        var targetWindow = JsInterop.FromEngineObject(a.This as JSObject ?? window);
        var sourceWindow = _host.ResolveCurrentWindow();
        var (targetOrigin, ports, cloneOptions, transferredPorts) = GetPostMessageDispatchOptions(a);
        if (!ShouldDeliverWindowMessage(targetWindow, sourceWindow, targetOrigin))
            return JSUndefined.Value;
        var payload = CloneForMessaging(a.Length > 0 ? a[0] : JSUndefined.Value, cloneOptions);
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
        return JSUndefined.Value;
    }

    private (string TargetOrigin, JsValue Ports, JSValue CloneOptions, List<JsValue> TransferredPorts) GetPostMessageDispatchOptions(in Arguments a)
    {
        var targetOrigin = "*";
        JSValue transferValue = JSUndefined.Value;

        if (a.Length > 1)
        {
            if (a[1] is JSObject optionsObject &&
                (optionsObject[(KeyString)"targetOrigin"] is { } ||
                 optionsObject[(KeyString)"transfer"] is { }))
            {
                var targetOriginValue = optionsObject[(KeyString)"targetOrigin"];
                if (targetOriginValue != null && !targetOriginValue.IsNullOrUndefined)
                    targetOrigin = targetOriginValue.ToString();

                transferValue = optionsObject[(KeyString)"transfer"] ?? JSUndefined.Value;
            }
            else
            {
                targetOrigin = a[1].ToString();
            }
        }

        if (a.Length > 2)
            transferValue = a[2];

        var (ports, cloneOptions, transferredPorts) = ExtractTransferList(transferValue);
        return (targetOrigin, ports, cloneOptions, transferredPorts);
    }

    /// <summary>
    /// Validates a transfer list and splits it into the ports the <c>MessageEvent</c> carries, the
    /// <c>{ transfer: [...] }</c> options the engine's <c>structuredClone</c> understands, and the
    /// ports whose owner window the send re-homes.
    /// </summary>
    /// <remarks>
    /// ENGINE-TYPED EDGE — see the second bullet in this class's remarks. Every decision it makes is
    /// about a type JSEAL does not model (<c>ArrayBuffer</c>, and whether one is detached), and the
    /// hole-skipping walk over the list has no JSEAL equivalent that keeps the same answer. Each
    /// failure now <see langword="throw"/>s <see cref="IJsCalls.DomError"/> where it used to call the
    /// bridge's <c>ThrowDOMException</c>, which threw the same <c>DOMException</c> — the difference
    /// is that the compiler can now see that the path ends, so the unreachable returns are gone.
    /// </remarks>
    private (JsValue Ports, JSValue CloneOptions, List<JsValue> TransferredPorts) ExtractTransferList(JSValue transferValue)
    {
        var realm = _host.Realm;

        if (transferValue.IsNullOrUndefined)
            return (realm.NewArray(), JSUndefined.Value, []);

        if (transferValue is not JSArray transferArray)
            throw realm.DomError("DataCloneError", "The transfer list contains a non-transferable value.");

        var transferredPorts = new List<JSValue>();
        var transferredPortHandles = new List<JsValue>();
        var seenPorts = new HashSet<JSObject>(ReferenceEqualityComparer.Instance);
        var transferredBuffers = new List<JSValue>();
        var seenBuffers = new HashSet<JSArrayBuffer>(ReferenceEqualityComparer.Instance);

        foreach (var (_, item) in transferArray.GetArrayElements(withHoles: false))
        {
            if (item is JSObject port && _messagePorts.HasPeer(JsInterop.FromEngineObject(port)))
            {
                if (!seenPorts.Add(port))
                    throw realm.DomError("DataCloneError", "The transfer list contains duplicate transferable values.");

                transferredPorts.Add(port);
                transferredPortHandles.Add(JsInterop.FromEngineObject(port));
                continue;
            }

            if (item is JSArrayBuffer arrayBuffer)
            {
                if (arrayBuffer.Detached)
                    throw realm.DomError("DataCloneError", "The transfer list contains a detached ArrayBuffer.");

                if (!seenBuffers.Add(arrayBuffer))
                    throw realm.DomError("DataCloneError", "The transfer list contains duplicate transferable values.");

                transferredBuffers.Add(arrayBuffer);
                continue;
            }

            throw realm.DomError("DataCloneError", "The transfer list contains a non-transferable value.");
        }

        JSValue cloneOptions = JSUndefined.Value;
        if (transferredBuffers.Count > 0)
        {
            var transferOptions = new JSObject();
            transferOptions.FastAddValue("transfer", new JSArray(transferredBuffers), JSPropertyAttributes.EnumerableConfigurableValue);
            cloneOptions = transferOptions;
        }

        return (JsInterop.FromEngineObject(new JSArray(transferredPorts)), cloneOptions, transferredPortHandles);
    }

    private void CommitTransferredPorts(IEnumerable<JsValue> transferredPorts, JsValue targetWindow)
    {
        foreach (var port in transferredPorts)
            _eventTargets.SetOwnerWindow(JsInterop.ToEngineObject(port), JsInterop.ToEngineObject(targetWindow));
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
    /// ENGINE-TYPED EDGE — see the first bullet in this class's remarks. JSEAL declares no
    /// structured-clone operation, and the clone's result may be a primitive, which a JSEAL handle
    /// cannot carry back across the seam. Both facts have to change together before this moves.
    /// </remarks>
    private JSValue CloneForMessaging(JSValue value, JSValue cloneOptions = default)
    {
        try
        {
            if (cloneOptions == null || cloneOptions.IsNullOrUndefined)
                return JavaScript.Globals.JSGlobalStatic.StructuredClone(new Arguments(JSUndefined.Value, value));

            return JavaScript.Globals.JSGlobalStatic.StructuredClone(new Arguments(JSUndefined.Value, value, cloneOptions));
        }
        catch (JSException)
        {
            throw _host.Realm.DomError("DataCloneError", "The object could not be cloned.");
        }
    }

    /// <summary>
    /// Builds the <c>MessageEvent</c> delivered to a window or to a port.
    /// </summary>
    /// <remarks>
    /// Every member but <c>data</c> is installed through the realm. <c>data</c> is the structured
    /// clone, which is an engine value for the reason <see cref="CloneForMessaging"/> gives, so it is
    /// installed on the engine's own object — in its original position, because property order is
    /// what <c>Object.keys</c> and a <c>for…in</c> over the event report.
    /// </remarks>
    private JsValue CreateMessageEvent(JSValue data, JsValue sourceWindow, string origin, JsValue ports)
    {
        var realm = _host.Realm;
        var evt = realm.NewObject();
        realm.DefineValue(evt, "type", JsValue.String("message"));
        realm.DefineValue(evt, "bubbles", JsValue.False);
        realm.DefineValue(evt, "cancelable", JsValue.False);
        realm.DefineValue(evt, "defaultPrevented", JsValue.False);
        JsInterop.ToEngineObject(evt).FastAddValue("data", data, JSPropertyAttributes.EnumerableConfigurableValue);
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
        var portObject = JsInterop.ToEngineObject(port);
        var effectiveOwner = FirstObject(ownerWindow, _host.WindowObject, _host.ResolveCurrentWindow(), port);
        _eventTargets.SetOwnerWindow(portObject, JsInterop.ToEngineObject(effectiveOwner));
        InstallEventTargetApi(portObject, "DomBridge.messagePort.dispatchEvent");
        JsValue onMessageHandler = JsValue.Null;

        // postMessage keeps its engine argument frame: it reads the payload the clone consumes, and a
        // clone has no JSEAL expression. Installed here rather than through the realm so that the
        // member order a page enumerates is the one it always was.
        portObject.FastAddValue("postMessage",
            new DomFunction((in a) => PortPostMessage(port, in a), "postMessage", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

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

    private JSValue PortPostMessage(JsValue port, in Arguments a)
    {
        var sourcePort = a.This is JSObject thisPort ? JsInterop.FromEngineObject(thisPort) : port;
        if (_messagePorts.IsClosed(sourcePort) || !_messagePorts.TryGetPeer(sourcePort, out var targetPort) || _messagePorts.IsClosed(targetPort))
        {
            return JSUndefined.Value;
        }

        JSValue transferValue = JSUndefined.Value;
        if (a.Length > 1)
        {
            if (a[1] is JSObject optionsObject && optionsObject[(KeyString)"transfer"] is { })
            {
                transferValue = optionsObject[(KeyString)"transfer"] ?? JSUndefined.Value;
            }
            else
            {
                transferValue = a[1];
            }
        }

        var targetOwner = FirstObject(_host.ResolveOwnerWindow(targetPort), _host.WindowObject, sourcePort);
        var (ports, cloneOptions, transferredPorts) = ExtractTransferList(transferValue);
        var payload = CloneForMessaging(a.Length > 0 ? a[0] : JSUndefined.Value, cloneOptions);
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
        return JSUndefined.Value;
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
