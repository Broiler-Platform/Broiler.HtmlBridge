using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Array.Typed;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;
using Broiler.HtmlBridge.Dom.Runtime;

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
/// field. The static, engine-neutral bridge helpers <c>DomBridge.InvokeEventListener</c> and
/// <c>DomBridge.ThrowDOMException</c> are called directly.
/// </summary>
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

    /// <summary>Installs <c>addEventListener</c>/<c>removeEventListener</c>/<c>dispatchEvent</c> on a
    /// generic event target (a message port or a sub-window).</summary>
    internal void InstallEventTargetApi(JSObject target, string logContext)
    {
        target.FastAddValue("addEventListener",
            new DomFunction((in a) => AddEventListener(target, in a), "addEventListener", 3),
            JSPropertyAttributes.EnumerableConfigurableValue);

        target.FastAddValue("removeEventListener",
            new DomFunction((in a) => RemoveEventListener(target, in a), "removeEventListener", 3),
            JSPropertyAttributes.EnumerableConfigurableValue);

        target.FastAddValue("dispatchEvent",
            new DomFunction((in a) => DispatchEvent(logContext, target, in a), "dispatchEvent", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);
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

    private JSValue DispatchEvent(string logContext, JSObject target, in Arguments a)
    {
        if (a.Length == 0 || a[0] is not JSObject evt)
            return JSBoolean.True;
        return DispatchEventTarget(target, evt, logContext);
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

    private JSValue DispatchEventTarget(JSObject target, JSObject evt, string logContext)
    {
        var eventType = evt[(KeyString)"type"]?.ToString() ?? "unknown";
        evt.FastAddValue("target", target, JSPropertyAttributes.EnumerableConfigurableValue);
        evt[(KeyString)"srcElement"] = target;
        evt.FastAddValue("currentTarget", target, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("eventPhase", new JSNumber(2), JSPropertyAttributes.EnumerableConfigurableValue);

        var immediateStopped = false;
        var prevented = evt[(KeyString)"defaultPrevented"] is JSValue defaultPreventedValue &&
                        defaultPreventedValue.BooleanValue;
        var currentListenerPassive = false;
        var legacyCancelBubble = false;
        evt[(KeyString)"defaultPrevented"] = prevented ? JSBoolean.True : JSBoolean.False;

        evt.FastAddValue("stopPropagation",
            new DomFunction((in _) => StopPropagation(ref legacyCancelBubble, in _), "stopPropagation", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        evt.FastAddValue("stopImmediatePropagation",
            new DomFunction((in _) => StopImmediatePropagation(ref immediateStopped, ref legacyCancelBubble, in _), "stopImmediatePropagation", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        evt.FastAddValue("preventDefault",
            new DomFunction((in _) => PreventDefault(currentListenerPassive, evt, ref prevented, in _), "preventDefault", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        evt.FastAddProperty("cancelBubble",
            new DomFunction((in _) => legacyCancelBubble ? JSBoolean.True : JSBoolean.False, "get cancelBubble"),
            new DomFunction((in setArgs) => SetCancelBubble(ref legacyCancelBubble, in setArgs), "set cancelBubble"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        evt.FastAddProperty("returnValue",
            new DomFunction((in _) => prevented ? JSBoolean.False : JSBoolean.True, "get returnValue"),
            new DomFunction((in setArgs) => SetReturnValue(currentListenerPassive, evt, ref prevented, in setArgs), "set returnValue"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        evt.FastAddValue("composedPath",
            new DomFunction((in _) => new JSArray(target), "composedPath", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        InvokeEventTargetHandler(target, eventType, evt, logContext);

        if (_eventTargets.TryGetTargetListeners(target, out var listenersByType) &&
            listenersByType.TryGetValue(eventType, out var listeners))
        {
            foreach (var registration in listeners.ToList())
            {
                if (immediateStopped)
                    break;

                currentListenerPassive = registration.Passive;
                InvokeEventListenerWithOwner(target, registration.Listener, evt, logContext);
                currentListenerPassive = false;

                if (registration.Once)
                    listeners.Remove(registration);
            }
        }

        evt[(KeyString)"currentTarget"] = JSNull.Value;
        evt[(KeyString)"eventPhase"] = new JSNumber(0);
        return prevented ? JSBoolean.False : JSBoolean.True;
    }

    private void InvokeEventTargetHandler(JSObject target, string eventType, JSObject evt, string logContext)
    {
        if (target[(KeyString)$"on{eventType}"] is not JSValue handler ||
            handler.IsNullOrUndefined)
        {
            return;
        }

        InvokeEventListenerWithOwner(target, handler, evt, logContext);
    }

    private void InvokeEventListenerWithOwner(JSObject target, JSValue listener, JSObject evt, string logContext)
    {
        var ownerWindow = _host.ResolveOwnerWindow(target);
        if (ownerWindow == null)
        {
            DomBridge.InvokeEventListener(listener, evt, logContext);
            return;
        }

        _host.RunWithWindowContext(ownerWindow, () => DomBridge.InvokeEventListener(listener, evt, logContext));
    }

    private static JSValue StopPropagation(ref bool legacyCancelBubble, in Arguments _)
    {
        legacyCancelBubble = true;
        return JSUndefined.Value;
    }

    private static JSValue StopImmediatePropagation(ref bool immediateStopped, ref bool legacyCancelBubble, in Arguments _)
    {
        immediateStopped = true;
        legacyCancelBubble = true;
        return JSUndefined.Value;
    }

    private static JSValue PreventDefault(bool currentListenerPassive, JSObject evt, ref bool prevented, in Arguments _)
    {
        var cancelable = evt[(KeyString)"cancelable"];
        if (!currentListenerPassive && cancelable != null && cancelable.BooleanValue)
        {
            prevented = true;
            evt[(KeyString)"defaultPrevented"] = JSBoolean.True;
        }

        return JSUndefined.Value;
    }

    private static JSValue SetCancelBubble(ref bool legacyCancelBubble, in Arguments setArgs)
    {
        if (setArgs.Length > 0 && setArgs[0].BooleanValue)
            legacyCancelBubble = true;
        return JSUndefined.Value;
    }

    private static JSValue SetReturnValue(bool currentListenerPassive, JSObject evt, ref bool prevented, in Arguments setArgs)
    {
        var cancelable = evt[(KeyString)"cancelable"];
        if (setArgs.Length > 0 && !setArgs[0].BooleanValue && !currentListenerPassive && cancelable != null && cancelable.BooleanValue)
        {
            prevented = true;
            evt[(KeyString)"defaultPrevented"] = JSBoolean.True;
        }

        return JSUndefined.Value;
    }

    // ==================== window.postMessage ====================

    /// <summary>Installs <c>window.postMessage</c> on <paramref name="window"/> (top window or a
    /// sub-window).</summary>
    internal void RegisterWindowMessaging(JSObject window)
    {
        window.FastAddValue(
            "postMessage",
            new DomFunction((in a) => WindowPostMessage(window, in a), "postMessage", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);
    }

    private JSValue WindowPostMessage(JSObject window, in Arguments a)
    {
        var targetWindow = a.This as JSObject ?? window;
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
            if (ReferenceEquals(targetWindow, _host.WindowJSObject))
            {
                _host.DispatchWindowEvent(evt);
            }
            else
            {
                _host.RunWithWindowContext(targetWindow, () => DispatchEventTarget(targetWindow, evt, "DomBridge.window.postMessage"));
            }
        });
        return JSUndefined.Value;
    }

    private (string TargetOrigin, JSArray Ports, JSValue CloneOptions, List<JSObject> TransferredPorts) GetPostMessageDispatchOptions(in Arguments a)
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

    private (JSArray Ports, JSValue CloneOptions, List<JSObject> TransferredPorts) ExtractTransferList(JSValue transferValue)
    {
        if (transferValue.IsNullOrUndefined)
            return (new JSArray(), JSUndefined.Value, []);

        if (transferValue is not JSArray)
        {
            DomBridge.ThrowDOMException(_host.JsContext!, "The transfer list contains a non-transferable value.", "DataCloneError");
            return (new JSArray(), JSUndefined.Value, []);
        }

        var transferArray = (JSArray)transferValue;

        var transferredPorts = new List<JSValue>();
        var transferredPortObjects = new List<JSObject>();
        var seenPorts = new HashSet<JSObject>(ReferenceEqualityComparer.Instance);
        var transferredBuffers = new List<JSValue>();
        var seenBuffers = new HashSet<JSArrayBuffer>(ReferenceEqualityComparer.Instance);

        foreach (var (_, item) in transferArray.GetArrayElements(withHoles: false))
        {
            if (item is JSObject port && _messagePorts.HasPeer(port))
            {
                if (!seenPorts.Add(port))
                    DomBridge.ThrowDOMException(_host.JsContext!, "The transfer list contains duplicate transferable values.", "DataCloneError");

                transferredPorts.Add(port);
                transferredPortObjects.Add(port);
                continue;
            }

            if (item is JSArrayBuffer arrayBuffer)
            {
                if (arrayBuffer.Detached)
                    DomBridge.ThrowDOMException(_host.JsContext!, "The transfer list contains a detached ArrayBuffer.", "DataCloneError");

                if (!seenBuffers.Add(arrayBuffer))
                    DomBridge.ThrowDOMException(_host.JsContext!, "The transfer list contains duplicate transferable values.", "DataCloneError");

                transferredBuffers.Add(arrayBuffer);
                continue;
            }

            DomBridge.ThrowDOMException(_host.JsContext!, "The transfer list contains a non-transferable value.", "DataCloneError");
        }

        JSValue cloneOptions = JSUndefined.Value;
        if (transferredBuffers.Count > 0)
        {
            var transferOptions = new JSObject();
            transferOptions.FastAddValue("transfer", new JSArray(transferredBuffers), JSPropertyAttributes.EnumerableConfigurableValue);
            cloneOptions = transferOptions;
        }

        return (new JSArray(transferredPorts), cloneOptions, transferredPortObjects);
    }

    private void CommitTransferredPorts(IEnumerable<JSObject> transferredPorts, JSObject targetWindow)
    {
        foreach (var port in transferredPorts)
            _eventTargets.SetOwnerWindow(port, targetWindow);
    }

    private bool ShouldDeliverWindowMessage(JSObject targetWindow, JSObject? sourceWindow, string targetOrigin)
    {
        if (string.IsNullOrWhiteSpace(targetOrigin) || targetOrigin == "*")
            return true;

        if (targetOrigin == "/")
            targetOrigin = GetWindowOrigin(sourceWindow);

        return string.Equals(targetOrigin, GetWindowOrigin(targetWindow), StringComparison.Ordinal);
    }

    private string GetWindowOrigin(JSObject? window)
    {
        if (window == null)
            return string.Empty;

        // The top window's origin is the document's, taken from the host rather than read back out
        // of `location`. The window IS the global object, and RunWithWindowContext swaps `location`
        // (and `document`/`self`/`parent`) to a frame's for the duration of that frame's scripts —
        // which is precisely when a frame calls parent.postMessage. Reading the property there would
        // see the frame's own about:srcdoc and report the parent as having no origin. Taking the
        // fact instead of the mutable view is also what terminates the walk below: a top-level
        // window is its own `parent`, so an about:blank page has no further parent to inherit from.
        if (ReferenceEquals(window, _host.WindowJSObject))
            return _host.PageOrigin;

        if (window[(KeyString)"location"] is JSObject location)
        {
            var href = location[(KeyString)"href"]?.ToString() ?? string.Empty;
            if (string.Equals(href, "about:srcdoc", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(href, "about:blank", StringComparison.OrdinalIgnoreCase))
            {
                // An about:blank / about:srcdoc document has no origin of its own and inherits its
                // parent's. A window that parents itself is the top of the tree and has nothing left
                // to inherit from, so it ends the walk rather than recurring forever.
                var parent = window[(KeyString)"parent"] as JSObject;
                return parent == null || ReferenceEquals(parent, window)
                    ? string.Empty
                    : GetWindowOrigin(parent);
            }

            var origin = location[(KeyString)"origin"]?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(origin))
                return origin;

            if (Uri.TryCreate(href, UriKind.Absolute, out var hrefUri))
                return Scripting.Origin.Of(hrefUri);
        }

        return string.Empty;
    }

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
            DomBridge.ThrowDOMException(_host.JsContext!, "The object could not be cloned.", "DataCloneError");
            return JSUndefined.Value;
        }
    }

    private static JSObject CreateMessageEvent(JSValue data, JSObject? sourceWindow, string origin, JSArray ports)
    {
        var evt = new JSObject();
        evt.FastAddValue("type", new JSString("message"), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("bubbles", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("cancelable", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("defaultPrevented", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("data", data, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("origin", new JSString(origin), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("lastEventId", new JSString(string.Empty), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("source", sourceWindow ?? JSNull.Value, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("ports", ports, JSPropertyAttributes.EnumerableConfigurableValue);
        return evt;
    }

    // ==================== MessageChannel / MessagePort ====================

    /// <summary>Constructs a <c>MessageChannel</c> with two entangled ports.</summary>
    internal JSObject CreateMessageChannel()
    {
        var ownerWindow = _host.ResolveCurrentWindow();
        var port1 = CreateMessagePort(ownerWindow);
        var port2 = CreateMessagePort(ownerWindow);
        _messagePorts.Link(port1, port2);

        var channel = new JSObject();
        channel.FastAddValue("port1", port1, JSPropertyAttributes.EnumerableConfigurableValue);
        channel.FastAddValue("port2", port2, JSPropertyAttributes.EnumerableConfigurableValue);
        return channel;
    }

    private JSObject CreateMessagePort(JSObject? ownerWindow)
    {
        var port = new JSObject();
        var effectiveOwner = ownerWindow ?? _host.WindowJSObject ?? _host.ResolveCurrentWindow() ?? port;
        _eventTargets.SetOwnerWindow(port, effectiveOwner);
        InstallEventTargetApi(port, "DomBridge.messagePort.dispatchEvent");
        JSValue onMessageHandler = JSNull.Value;

        port.FastAddValue("postMessage",
            new DomFunction((in a) => PortPostMessage(port, in a), "postMessage", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        port.FastAddProperty("onmessage",
            new DomFunction((in _) => onMessageHandler, "get onmessage"),
            new DomFunction((in a) => SetOnMessage(ref onMessageHandler, port, in a), "set onmessage"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        port.FastAddValue("start",
            new DomFunction((in a) => StartPort(port, in a), "start", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        port.FastAddValue("close",
            new DomFunction((in a) => ClosePort(port, in a), "close", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        return port;
    }

    private JSValue PortPostMessage(JSObject? port, in Arguments a)
    {
        var sourcePort = a.This as JSObject ?? port;
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

        var targetOwner = _host.ResolveOwnerWindow(targetPort) ?? _host.WindowJSObject ?? sourcePort;
        var (ports, cloneOptions, transferredPorts) = ExtractTransferList(transferValue);
        var payload = CloneForMessaging(a.Length > 0 ? a[0] : JSUndefined.Value, cloneOptions);
        CommitTransferredPorts(transferredPorts, targetOwner);
        _host.QueueFrameAction(() =>
        {
            if (_messagePorts.IsClosed(sourcePort) || _messagePorts.IsClosed(targetPort))
            {
                return;
            }

            var evt = CreateMessageEvent(payload, null, string.Empty, ports);
            DispatchOrQueueMessagePortEvent(targetPort, evt);
        });
        return JSUndefined.Value;
    }

    private JSValue SetOnMessage(ref JSValue onMessageHandler, JSObject? port, in Arguments a)
    {
        onMessageHandler = a.Length > 0 ? a[0] : JSUndefined.Value;
        if (!onMessageHandler.IsNullOrUndefined)
        {
            ActivateMessagePort(a.This as JSObject ?? port);
        }

        return JSUndefined.Value;
    }

    private JSValue StartPort(JSObject? port, in Arguments a)
    {
        ActivateMessagePort(a.This as JSObject ?? port);
        return JSUndefined.Value;
    }

    private JSValue ClosePort(JSObject? port, in Arguments a)
    {
        var currentPort = a.This as JSObject ?? port;
        _messagePorts.Close(currentPort);
        return JSUndefined.Value;
    }

    private void DispatchOrQueueMessagePortEvent(JSObject targetPort, JSObject evt)
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

    private bool CanDispatchMessagePortEvent(JSObject targetPort)
        => _messagePorts.IsStarted(targetPort) || HasOnMessageHandler(targetPort);

    private static bool HasOnMessageHandler(JSObject targetPort)
        => targetPort[(KeyString)"onmessage"] is { } handler && !handler.IsNullOrUndefined;

    private void ActivateMessagePort(JSObject port)
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

    private void DispatchMessagePortEvent(JSObject targetPort, JSObject evt)
    {
        var targetOwner = _host.ResolveOwnerWindow(targetPort);
        if (targetOwner == null)
        {
            DispatchEventTarget(targetPort, evt, "DomBridge.messagePort.postMessage");
        }
        else
        {
            _host.RunWithWindowContext(targetOwner, () =>
                DispatchEventTarget(targetPort, evt, "DomBridge.messagePort.postMessage"));
        }
    }
}
