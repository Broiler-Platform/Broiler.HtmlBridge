using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// The single owner of a document's <c>MessageChannel</c>/<c>MessagePort</c> state (HtmlBridge
/// complexity-reduction roadmap Phase 2, P2.6 — the ports slice of the browsing-context state): the
/// entangled port peers, which ports are closed, which are started (their queue is draining), and the
/// per-port queue of messages awaiting a started port. It replaces the four port maps that were
/// scattered across the messaging code.
/// </summary>
/// <remarks>
/// Ports are reference-keyed (a port's identity is its JS object). The messaging callbacks still build
/// and dispatch the JS <c>MessageEvent</c>s; they read and mutate port state through here. Instance-
/// scoped to the owning bridge/document; <see cref="Clear"/> runs on re-parse and disposal.
/// </remarks>
internal sealed class MessagePortRegistry
{
    private readonly Dictionary<JSObject, JSObject> _peers = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<JSObject> _closed = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<JSObject> _started = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<JSObject, List<JSObject>> _queued = new(ReferenceEqualityComparer.Instance);

    /// <summary>Entangles the two ends of a <c>MessageChannel</c> so each is the other's peer.</summary>
    public void Link(JSObject a, JSObject b)
    {
        _peers[a] = b;
        _peers[b] = a;
    }

    /// <summary>The peer entangled with <paramref name="port"/>, if any.</summary>
    public bool TryGetPeer(JSObject port, out JSObject peer) => _peers.TryGetValue(port, out peer!);

    /// <summary>Whether <paramref name="port"/> is one end of an entangled channel (a transferable port).</summary>
    public bool HasPeer(JSObject port) => _peers.ContainsKey(port);

    public bool IsClosed(JSObject port) => _closed.Contains(port);

    /// <summary>Closes <paramref name="port"/> and drops any messages queued for it.</summary>
    public void Close(JSObject port)
    {
        _closed.Add(port);
        _queued.Remove(port);
    }

    public bool IsStarted(JSObject port) => _started.Contains(port);

    public void Start(JSObject port) => _started.Add(port);

    /// <summary>Queues a message event for a not-yet-started <paramref name="port"/>.</summary>
    public void Enqueue(JSObject port, JSObject messageEvent)
    {
        if (!_queued.TryGetValue(port, out var events))
        {
            events = [];
            _queued[port] = events;
        }

        events.Add(messageEvent);
    }

    /// <summary>
    /// Removes and returns the messages queued for <paramref name="port"/> (to deliver when it
    /// starts), or <c>null</c> when there are none.
    /// </summary>
    public List<JSObject>? TakeQueued(JSObject port)
    {
        if (!_queued.TryGetValue(port, out var events) || events.Count == 0)
            return null;

        _queued.Remove(port);
        return events;
    }

    /// <summary>Drops all port peers, closed/started marks and queued messages.</summary>
    public void Clear()
    {
        _peers.Clear();
        _closed.Clear();
        _started.Clear();
        _queued.Clear();
    }
}
