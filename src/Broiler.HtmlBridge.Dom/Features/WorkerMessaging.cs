using System;
using System.Collections.Generic;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Logging;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <c>MessageChannel</c> and <c>MessagePort</c> in a worker's realm: channels between two ports of
/// the worker, and ports entangled with ports of the page, or of another worker, across threads.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the worker's ports are the host's now.</b> They were a script of the worker's own
/// (<c>worker-message-channel.js</c>), which kept both ends of a channel inside the worker: a port
/// could not be transferred to the page, and a worker that tried got a <c>DataCloneError</c>. That is
/// exactly what reCAPTCHA's worker does -- it hands the page one port of a channel of its own and
/// talks through it. A port that crosses to another thread has to be one the host can recognise in a
/// transfer list, leave behind and recreate on the other side, which a script's object is not.
/// </para>
/// <para>
/// <b>A message is a task of the worker's event loop</b>, never delivered during the post: one per
/// message, queued on the worker's inbox, each followed by the worker's microtask checkpoint. A
/// message between two ports of the worker is cloned in the worker's realm when it is posted; one for
/// a port on another thread is the sender's clone, which belongs to no realm, adopted here.
/// </para>
/// <para>
/// <b>Started, as on the page.</b> A port listened to with <c>addEventListener</c> holds its
/// messages until <c>start()</c>; setting <c>onmessage</c> starts it.
/// </para>
/// <para>
/// Everything here runs on the worker's thread, but for <see cref="IPortHome.Schedule"/>, which a
/// page thread calls to say a message is waiting, and which only queues a task.
/// </para>
/// </remarks>
internal sealed class WorkerMessaging
{
    private readonly IJsRealm _realm;
    private readonly Func<Action, bool> _queueTask;
    private readonly Action<string> _reportError;
    private readonly string _label;
    private readonly Dictionary<JsValue, Port> _ports = [];

    /// <param name="realm">The worker's realm.</param>
    /// <param name="queueTask">
    /// Queues a task on the worker's event loop from any thread. Answers whether it was queued: a
    /// closed worker takes nothing.
    /// </param>
    /// <param name="reportError">Reports what a message listener threw, as the worker's <c>error</c>.</param>
    /// <param name="label">The worker, as log entries name it.</param>
    public WorkerMessaging(IJsRealm realm, Func<Action, bool> queueTask, Action<string> reportError, string label)
    {
        _realm = realm;
        _queueTask = queueTask;
        _reportError = reportError;
        _label = label;
    }

    /// <summary>One port of this worker.</summary>
    private sealed class Port(JsValue handle)
    {
        public readonly JsValue Handle = handle;

        /// <summary>The peer, when it is a port of this worker too.</summary>
        public Port? Peer;

        /// <summary>The end the peer is reached through, when it is on another thread.</summary>
        public PortEnd? End;

        public WorkerPortHome? Home;

        /// <summary>Where the port went, once it was transferred to another thread.</summary>
        public PortEnd? ShippedTo;

        public bool Started;
        public bool Closed;
        public JsValue OnMessage = JsValue.Null;
        public readonly List<JsValue> Listeners = [];

        /// <summary>Messages from the peer in this worker that the port has not dispatched yet.</summary>
        public readonly Queue<(JsValue Data, JsValue[] Ports)> Waiting = new();

        public bool Inert => Closed || ShippedTo is not null;
    }

    /// <summary>Installs <c>MessageChannel</c> and <c>MessagePort</c> on the worker's global.</summary>
    public void Install(JsValue global)
    {
        _realm.SetProperty(global, "MessageChannel", _realm.NewConstructor("MessageChannel", (in _) => CreateChannel(), 0));

        // There to be detected, as in a browser, and not to be constructed: a port comes from a channel.
        _realm.SetProperty(global, "MessagePort", _realm.NewConstructor("MessagePort",
            (in call) => throw call.Realm.Error(JsErrorKind.TypeError, "Illegal constructor"), 0));
    }

    /// <summary>Whether <paramref name="value"/> is a port of this worker that may be transferred.</summary>
    public bool IsPort(JsValue value) =>
        value.IsObject && _ports.TryGetValue(value, out var port) && port.ShippedTo is null;

    private JsValue CreateChannel()
    {
        var first = CreatePort();
        var second = CreatePort();
        first.Peer = second;
        second.Peer = first;

        var channel = _realm.NewObject();
        _realm.DefineValue(channel, "port1", first.Handle);
        _realm.DefineValue(channel, "port2", second.Handle);
        return channel;
    }

    private Port CreatePort()
    {
        var handle = _realm.NewObject();
        var port = new Port(handle);
        _ports[handle] = port;

        _realm.DefineMethod(handle, "postMessage", 1, (in call) => PostMessage(port, in call));
        _realm.DefineAccessor(handle, "onmessage",
            (in _) => port.OnMessage,
            (in call) =>
            {
                var handler = call.Length > 0 ? call[0] : JsValue.Null;
                port.OnMessage = handler.IsFunction ? handler : JsValue.Null;
                if (port.OnMessage.IsFunction)
                    Start(port);

                return JsValue.Undefined;
            });
        _realm.DefineMethod(handle, "start", (in _) => { Start(port); return JsValue.Undefined; });
        _realm.DefineMethod(handle, "close", (in _) => { Close(port); return JsValue.Undefined; });
        _realm.DefineMethod(handle, "addEventListener", 2, (in call) =>
        {
            if (call.Length > 1 && call[1].IsFunction &&
                string.Equals(call.Realm.ToJsString(call[0]), "message", StringComparison.Ordinal) &&
                !port.Listeners.Contains(call[1]))
            {
                port.Listeners.Add(call[1]);
            }

            return JsValue.Undefined;
        });
        _realm.DefineMethod(handle, "removeEventListener", 2, (in call) =>
        {
            if (call.Length > 1 && string.Equals(call.Realm.ToJsString(call[0]), "message", StringComparison.Ordinal))
                port.Listeners.Remove(call[1]);

            return JsValue.Undefined;
        });

        return port;
    }

    private JsValue PostMessage(Port source, in JsCall call)
    {
        var realm = call.Realm;
        if (source.Inert)
            return JsValue.Undefined;

        var (buffers, transferred) = WorkerTransfer.BuildTransferList(realm, call.Length > 1 ? call[1] : JsValue.Undefined, IsPort);
        if (transferred.Contains(source.Handle))
            throw realm.DomError("DataCloneError", "A port cannot be transferred through itself.");

        var message = call.Length > 0 ? call[0] : JsValue.Undefined;
        if (source.End is { } end)
        {
            JsDetachedValue detached;
            try
            {
                detached = realm.Detach(message, buffers);
            }
            catch (JsEngineException)
            {
                throw realm.DomError("DataCloneError", "The object could not be cloned.");
            }

            end.Post(new PortMessage(detached, ExportAll(transferred)));
            return JsValue.Undefined;
        }

        if (source.Peer is not { } target || target.Closed)
            return JsValue.Undefined;

        JsValue data;
        try
        {
            data = realm.Clone(message, buffers);
        }
        catch (JsEngineException)
        {
            throw realm.DomError("DataCloneError", "The object could not be cloned.");
        }

        target.Waiting.Enqueue((data, [.. transferred]));
        _queueTask(() => DeliverOne(target));
        return JsValue.Undefined;
    }

    /// <summary>Transfers each of <paramref name="handles"/>, ports of this worker, to another thread.</summary>
    public PortEnd[] ExportAll(IReadOnlyList<JsValue> handles)
    {
        var ends = new PortEnd[handles.Count];
        for (var i = 0; i < ends.Length; i++)
            ends[i] = Export(handles[i]);

        return ends;
    }

    /// <summary>
    /// Transfers a port of this worker to another thread: the object stays behind, inert, and the end
    /// it is reached through from now on is what travels, with the messages it had not dispatched.
    /// </summary>
    private PortEnd Export(JsValue handle)
    {
        var port = _ports[handle];
        PortEnd end;
        if (port.End is { } remote)
        {
            remote.Leave(port.Home!);
            end = remote;
        }
        else
        {
            var (traveling, staying) = PortEnd.NewPair();
            end = traveling;
            if (port.Peer is { } peer)
            {
                peer.Peer = null;
                if (peer.Closed)
                    staying.Close();
                else
                    LinkAcross(peer, staying);
            }
        }

        if (port.Waiting.Count > 0)
        {
            var undelivered = new List<PortMessage>(port.Waiting.Count);
            while (port.Waiting.TryDequeue(out var waiting))
            {
                try
                {
                    var data = _realm.Detach(waiting.Data);
                    var carried = new List<JsValue>();
                    foreach (var carriedPort in waiting.Ports)
                    {
                        if (IsPort(carriedPort))
                            carried.Add(carriedPort);
                    }

                    undelivered.Add(new PortMessage(data, ExportAll(carried)));
                }
                catch (JsEngineException ex)
                {
                    RenderLogger.LogError(LogCategory.JavaScript, "WorkerMessaging.Export",
                        $"Worker '{_label}': a queued message could not follow its port to another thread: {ex.Message}", ex);
                }
            }

            end.Requeue(undelivered);
        }

        if (port.Closed)
            end.Close();

        port.ShippedTo = end;
        port.Peer = null;
        port.End = null;
        port.Home = null;
        return end;
    }

    /// <summary>A port of another thread arriving in this worker, with a message: a new port object for <paramref name="end"/>.</summary>
    public JsValue Import(PortEnd end)
    {
        var port = CreatePort();
        if (end.IsClosed)
            port.Closed = true;
        else
            LinkAcross(port, end);

        return port.Handle;
    }

    private void LinkAcross(Port port, PortEnd end)
    {
        port.Peer = null;
        port.End = end;
        port.Home = new WorkerPortHome(this, port);
        end.Settle(port.Home);
    }

    private void Start(Port port)
    {
        if (port.Inert || port.Started)
            return;

        port.Started = true;
        for (var i = 0; i < port.Waiting.Count; i++)
            _queueTask(() => DeliverOne(port));

        port.End?.ScheduleWaiting();
    }

    private static void Close(Port port)
    {
        port.Closed = true;
        port.Waiting.Clear();
        port.End?.Close();
    }

    /// <summary>
    /// Dispatches the next message for <paramref name="port"/>, if it is started: one from its peer in
    /// this worker first, since those were posted before its peer left, then one from across threads.
    /// </summary>
    private void DeliverOne(Port port)
    {
        if (port.Inert || !port.Started)
            return;

        if (port.Waiting.TryDequeue(out var local))
        {
            Dispatch(port, local.Data, local.Ports);
            return;
        }

        if (port.End is not { } end || port.Home is not { } home || !end.TryTake(home, out var message))
            return;

        JsValue data;
        try
        {
            data = _realm.Adopt(message.Data);
        }
        catch (JsEngineException ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "WorkerMessaging.DeliverOne",
                $"Worker '{_label}': a message from another thread could not be materialized: {ex.Message}", ex);
            return;
        }

        var ports = new JsValue[message.Ports.Length];
        for (var i = 0; i < ports.Length; i++)
            ports[i] = Import(message.Ports[i]);

        Dispatch(port, data, ports);
    }

    private void Dispatch(Port port, JsValue data, JsValue[] ports)
    {
        var evt = _realm.NewObject();
        _realm.DefineValue(evt, "type", JsValue.String("message"));
        _realm.DefineValue(evt, "data", data);
        _realm.DefineValue(evt, "origin", JsValue.String(string.Empty));
        _realm.DefineValue(evt, "lastEventId", JsValue.String(string.Empty));
        _realm.DefineValue(evt, "source", JsValue.Null);
        _realm.DefineValue(evt, "ports", _realm.NewArray(ports));
        _realm.DefineValue(evt, "target", port.Handle);
        _realm.DefineValue(evt, "currentTarget", port.Handle);

        try
        {
            if (port.OnMessage.IsFunction)
                _realm.Invoke(port.OnMessage, port.Handle, [evt]);

            foreach (var listener in port.Listeners.ToArray())
                _realm.Invoke(listener, port.Handle, [evt]);
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "WorkerMessaging.Dispatch",
                $"Worker '{_label}': a port's message listener threw: {ex.Message}", ex);
            _reportError($"Worker message handler error: {ex.Message}");
        }
    }

    /// <summary>
    /// Closes every port of this worker entangled with one on another thread: the worker is gone, and
    /// what the other side posts afterwards goes nowhere.
    /// </summary>
    public void CloseAll()
    {
        foreach (var port in _ports.Values)
            port.End?.Close();

        _ports.Clear();
    }

    /// <summary>The worker, as the realm a port is in: a message waiting for the port is a task on its inbox.</summary>
    private sealed class WorkerPortHome(WorkerMessaging owner, Port port) : IPortHome
    {
        public void Schedule(PortEnd end) => owner._queueTask(() => owner.DeliverOne(port));
    }
}
