using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// The single owner of a document's <c>MessageChannel</c>/<c>MessagePort</c> state (HtmlBridge
/// complexity-reduction roadmap Phase 2, P2.6 — the ports slice of the browsing-context state): the
/// entangled port peers, which ports are closed, which are started (their queue is draining), and the
/// per-port queue of messages awaiting a started port. It replaces the four port maps that were
/// scattered across the messaging code.
/// </summary>
/// <remarks>
/// <para>
/// Ports are reference-keyed (a port's identity is its JS object). The messaging callbacks still build
/// and dispatch the JS <c>MessageEvent</c>s; they read and mutate port state through here. Instance-
/// scoped to the owning bridge/document; <see cref="Clear"/> runs on re-parse and disposal.
/// </para>
/// <para>
/// <b>The key is a <see cref="JsValue"/> handle and the comparer went away with the engine type.</b>
/// The maps used to be built with <c>ReferenceEqualityComparer.Instance</c> over the engine's own
/// object type, because a port's identity is the object and not anything it holds. A handle answers
/// exactly that question already — <see cref="JsValue.Equals(JsValue)"/> compares the engine's own
/// instance by reference for every object kind — so the default comparer is the reference comparer
/// for the values this type stores, and asking for one explicitly would only restate it.
/// </para>
/// </remarks>
internal sealed class MessagePortRegistry
{
    private readonly Dictionary<JsValue, JsValue> _peers = [];
    private readonly HashSet<JsValue> _closed = [];
    private readonly HashSet<JsValue> _started = [];
    private readonly Dictionary<JsValue, List<JsValue>> _queued = [];

    /// <summary>Entangles the two ends of a <c>MessageChannel</c> so each is the other's peer.</summary>
    public void Link(JsValue a, JsValue b)
    {
        _peers[a] = b;
        _peers[b] = a;
    }

    /// <summary>The peer entangled with <paramref name="port"/>, if any.</summary>
    public bool TryGetPeer(JsValue port, out JsValue peer) => _peers.TryGetValue(port, out peer!);

    /// <summary>Whether <paramref name="port"/> is one end of an entangled channel (a transferable port).</summary>
    public bool HasPeer(JsValue port) => _peers.ContainsKey(port);

    public bool IsClosed(JsValue port) => _closed.Contains(port);

    /// <summary>Closes <paramref name="port"/> and drops any messages queued for it.</summary>
    public void Close(JsValue port)
    {
        _closed.Add(port);
        _queued.Remove(port);
    }

    public bool IsStarted(JsValue port) => _started.Contains(port);

    public void Start(JsValue port) => _started.Add(port);

    /// <summary>Queues a message event for a not-yet-started <paramref name="port"/>.</summary>
    public void Enqueue(JsValue port, JsValue messageEvent)
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
    public List<JsValue>? TakeQueued(JsValue port)
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
