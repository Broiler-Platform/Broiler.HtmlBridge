using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// The single owner of a document's <c>MessageChannel</c>/<c>MessagePort</c> state: the
/// entangled port peers, which ports are closed, which are started (their queue is draining), and the
/// per-port queue of messages awaiting a started port.
/// </summary>
/// <remarks>
/// <para>
/// Ports are reference-keyed (a port's identity is its JS object). The messaging callbacks still build
/// and dispatch the JS <c>MessageEvent</c>s; they read and mutate port state through here. Instance-
/// scoped to the owning bridge/document; <see cref="Clear"/> runs on re-parse and disposal.
/// </para>
/// <para>
/// <b>The key is a <see cref="JsValue"/> handle and the maps take no explicit comparer.</b> A port's
/// identity is the object and not anything it holds, and a handle answers exactly that question —
/// <see cref="JsValue.Equals(JsValue)"/> compares the engine's own instance by reference for every
/// object kind — so the default comparer is the reference comparer for the values this type stores,
/// and asking for one explicitly would only restate it.
/// </para>
/// <para>
/// <b>A port's peer is either here or on another thread.</b> A port whose peer is in this realm is
/// in the peer map; one whose peer is in a worker holds a <see cref="PortEnd"/> instead, which keeps
/// its message queue. A port transferred to a worker stays behind as an inert object, shipped.
/// </para>
/// <para>
/// <b>A message posted to a port of this realm waits at the port until its task runs</b>, rather
/// than riding in the task: a port transferred to a worker in the meantime takes it along, in its
/// place ahead of the messages posted after the transfer.
/// </para>
/// </remarks>
internal sealed class MessagePortRegistry
{
    private readonly Dictionary<JsValue, JsValue> _peers = [];
    private readonly Dictionary<JsValue, (PortEnd End, IPortHome Home)> _remote = [];
    private readonly HashSet<JsValue> _shipped = [];
    private readonly HashSet<JsValue> _closed = [];
    private readonly HashSet<JsValue> _started = [];
    private readonly Dictionary<JsValue, List<JsValue>> _queued = [];
    private readonly Dictionary<JsValue, Queue<PendingPortMessage>> _inFlight = [];

    /// <summary>Entangles the two ends of a <c>MessageChannel</c> so each is the other's peer.</summary>
    public void Link(JsValue a, JsValue b)
    {
        _peers[a] = b;
        _peers[b] = a;
    }

    /// <summary>The peer entangled with <paramref name="port"/>, if any.</summary>
    public bool TryGetPeer(JsValue port, out JsValue peer) => _peers.TryGetValue(port, out peer!);

    /// <summary>Whether <paramref name="port"/> is a port of this realm that may be transferred.</summary>
    public bool IsPort(JsValue port) => _peers.ContainsKey(port) || _remote.ContainsKey(port);

    /// <summary>Entangles <paramref name="port"/> with a port on another thread, through <paramref name="end"/>.</summary>
    public void LinkRemote(JsValue port, PortEnd end, IPortHome home)
    {
        _peers.Remove(port);
        _remote[port] = (end, home);
    }

    /// <summary>The end through which <paramref name="port"/> reaches a peer on another thread, if it does.</summary>
    public bool TryGetRemote(JsValue port, out PortEnd end, out IPortHome home)
    {
        if (_remote.TryGetValue(port, out var remote))
        {
            (end, home) = remote;
            return true;
        }

        end = null!;
        home = null!;
        return false;
    }

    /// <summary>
    /// Leaves <paramref name="port"/> behind as the inert object a transferred port is: no peer, no
    /// queue, and nothing it posts goes anywhere.
    /// </summary>
    public void Ship(JsValue port)
    {
        _peers.Remove(port);
        _remote.Remove(port);
        _started.Remove(port);
        _queued.Remove(port);
        _inFlight.Remove(port);
        _shipped.Add(port);
    }

    public bool IsClosed(JsValue port) => _closed.Contains(port);

    /// <summary>Whether <paramref name="port"/> can no longer send or receive here: closed, or transferred away.</summary>
    public bool IsInert(JsValue port) => _closed.Contains(port) || _shipped.Contains(port);

    /// <summary>Closes <paramref name="port"/> and drops any messages queued for it.</summary>
    public void Close(JsValue port)
    {
        _closed.Add(port);
        _queued.Remove(port);
        _inFlight.Remove(port);
        if (_remote.TryGetValue(port, out var remote))
            remote.End.Close();
    }

    /// <summary>A message posted to <paramref name="port"/> from its peer here, waiting for the task that delivers it.</summary>
    public void Send(JsValue port, PendingPortMessage message) =>
        RegistryMaps.GetOrAdd(_inFlight, port, static () => new Queue<PendingPortMessage>()).Enqueue(message);

    /// <summary>The next message posted to <paramref name="port"/>, for the task delivering it.</summary>
    public bool TryTakeInFlight(JsValue port, out PendingPortMessage message)
    {
        if (_inFlight.TryGetValue(port, out var messages) && messages.TryDequeue(out message))
            return true;

        message = default;
        return false;
    }

    /// <summary>Removes and returns every message still on its way to <paramref name="port"/>, in order.</summary>
    public Queue<PendingPortMessage>? TakeInFlight(JsValue port) =>
        _inFlight.Remove(port, out var messages) && messages.Count > 0 ? messages : null;

    public bool IsStarted(JsValue port) => _started.Contains(port);

    public void Start(JsValue port) => _started.Add(port);

    /// <summary>Queues a message event for a not-yet-started <paramref name="port"/>.</summary>
    public void Enqueue(JsValue port, JsValue messageEvent) =>
        RegistryMaps.GetOrAdd(_queued, port, static () => new List<JsValue>()).Add(messageEvent);

    /// <summary>
    /// Removes and returns the messages queued for <paramref name="port"/> (to deliver when it
    /// starts), or <c>null</c> when there are none.
    /// </summary>
    public List<JsValue>? TakeQueued(JsValue port)
    {
        if (!_queued.TryGetValue(port, out var events) || events.Count == 0)
            return null;

        _queued.Remove(port);
        return events;
    }

    /// <summary>
    /// Drops all port peers, closed/started marks and queued messages. A port entangled with one on
    /// another thread is closed, so that a worker stops posting to a document that is gone.
    /// </summary>
    public void Clear()
    {
        foreach (var (end, _) in _remote.Values)
            end.Close();

        _peers.Clear();
        _remote.Clear();
        _shipped.Clear();
        _closed.Clear();
        _started.Clear();
        _queued.Clear();
        _inFlight.Clear();
    }
}

/// <summary>
/// A message between two ports of one realm, from the post to the task that delivers it: the port
/// that sent it, its clone, and the array of ports transferred with it.
/// </summary>
internal readonly record struct PendingPortMessage(JsValue Source, JsValue Data, JsValue Ports);
