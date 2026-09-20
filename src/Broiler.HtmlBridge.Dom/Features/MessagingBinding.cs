using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The web-messaging feature binding module.
/// It co-locates the whole feature: <c>window.postMessage</c>, <c>MessageChannel</c>/<c>MessagePort</c>
/// (creation, <c>postMessage</c>, <c>start</c>/<c>close</c>/<c>onmessage</c>, the port message queue),
/// structured-clone/transfer-list handling and <c>MessageEvent</c> construction. It <b>owns</b> the
/// <see cref="MessagePortRegistry"/> state authority (entangled peers, closed/started marks
/// and the per-port pending-message queue).
///
/// It also owns the generic <c>EventTarget</c> dispatch (<c>addEventListener</c>/
/// <c>removeEventListener</c>/<c>dispatchEvent</c> with capture/target/bubble-free propagation control)
/// that is installed on message ports <em>and</em> on sub-windows — the two non-node event targets in
/// the bridge. That dispatch is co-located here (its listeners already come from the shared
/// <see cref="EventTargetRegistry"/>) pending a dedicated generic-EventTarget/Window module; sub-window
/// installation goes through the module's <see cref="InstallEventTargetApi"/> entry point.
/// That dispatch is in <c>MessagingBinding.EventTarget.cs</c>.
///
/// The module depends on the shared <see cref="EventTargetRegistry"/> (generic-target listeners +
/// owner-window map, which it does not own) and reaches the document's browsing-context operations —
/// window resolution, the window-context switch, frame-action queueing and top-window dispatch —
/// through the narrow <see cref="IMessagingHost"/> contract. It never touches an arbitrary bridge
/// field. The static bridge helper <c>DomBridge.InvokeEventListener</c>, which takes the listener and
/// the event as the handles this module holds (see the remarks), is called directly; a
/// <c>DataCloneError</c> is raised through <see cref="IJsCalls.DomError"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole feature speaks JSEAL.</b> Everything that builds or routes a message -- the ports, the channel, the
/// <c>MessageEvent</c>, the origin comparison, the pending-message queue, the structured clone, the
/// transfer list and the whole <see cref="IMessagingHost"/> contract -- speaks <see cref="IJsRealm"/>
/// and names no engine type. The clone is <see cref="IJsClone.Clone"/>, which is the same engine
/// algorithm reached through the realm that owns it rather than through a static; the transfer list is
/// <see cref="IJsClone.ClassifyTransferable"/> plus <c>WorkerTransfer.ArrayElements</c>, which is the
/// hole-skipping walk expressed as the own-enumerable-index question it always was.
/// </para>
/// <para>
/// <see cref="AddEventListener"/> and <see cref="RemoveEventListener"/> both take a
/// <see cref="JsCall"/>, and <see cref="InstallEventTargetApi"/> mints them through the realm.
/// <c>EventListenerRegistration.Listener</c>, declared in <c>DomBridge/RuntimeStates.cs</c> and
/// shared with the element, document, window and form-submit paths, holds a <see cref="JsValue"/>:
/// the two operations call <see cref="EventListenerBinding"/> with the call frame's realm, the
/// shared listener invoker takes the handle, and the <c>on…</c> handler is read through the realm.
/// </para>
/// </remarks>
internal sealed partial class MessagingBinding(IMessagingHost host, EventTargetRegistry eventTargets)
{
    private readonly IMessagingHost _host = host;
    private readonly EventTargetRegistry _eventTargets = eventTargets;

    // State authority for MessageChannel/MessagePort (peers, closed/started marks, queued
    // messages). Owned here because the whole messaging feature is co-located.
    private readonly MessagePortRegistry _messagePorts = new();

    /// <summary>Releases all message-channel/port state (called by the bridge's session reset).</summary>
    internal void ClearPorts() => _messagePorts.Clear();

    // ==================== window.postMessage ====================

    /// <summary>Installs <c>window.postMessage</c> on <paramref name="window"/> (top window or a
    /// sub-window).</summary>
    /// <remarks>
    /// <b>The parameter was engine-typed on the strength of a pin neither caller supplied.</b> The
    /// remark here said it was pinned twice over, by two callers that both hand this the engine's
    /// window object. Neither did. <c>DomBridge/Registration/Window.cs</c> holds
    /// <c>realm.Global</c> and was unwrapping it on the argument; <see cref="SubWindowBinding"/>
    /// mints its window with <see cref="IJsValues.NewObject"/> and unwrapped that. Both
    /// conversions existed only to satisfy this signature, and this method's first statement
    /// wrapped the object straight back up. The member is still installed through the realm, as a
    /// <see cref="IJsValues.NewMethod"/> because the member it replaces was built non-constructable
    /// and staying so is what makes this a refactor.
    /// </remarks>
    internal void RegisterWindowMessaging(JsValue window)
    {
        _host.Realm.DefineValue(window, "postMessage",
            _host.Realm.NewMethod("postMessage", (in call) => WindowPostMessage(window, in call), 2));
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
    /// Each failure <see langword="throw"/>s <see cref="IJsCalls.DomError"/>; the compiler can see
    /// that the path ends there, so no unreachable return follows one.
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
        // The realm is asked whether it can clone before it is asked to. Without this the refusal
        // for an engine that cannot -- JsCapabilityUnavailableException, which the catch below
        // cannot see -- left this method as a raw host exception thrown through page script, where
        // a DataCloneError is what postMessage promises.
        if (!WorkerTransfer.CanStructuredClone(_host.Realm))
            throw _host.Realm.DomError("DataCloneError", WorkerTransfer.EngineCannotCloneMessage);

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
    /// The owner-window branch is <see cref="RunInOwnerWindow"/>, the same two-case decision the
    /// listener paths make.
    /// </remarks>
    private void DispatchMessagePortEvent(JsValue targetPort, JsValue evt) =>
        RunInOwnerWindow(targetPort, () =>
            DispatchEventTarget(targetPort, evt, "DomBridge.messagePort.postMessage"));
}
