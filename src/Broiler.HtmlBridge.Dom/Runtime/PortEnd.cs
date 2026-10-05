using System.Collections.Generic;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// One end of a <c>MessageChannel</c> whose two ports are in different realms -- the page's and a
/// worker's, or two workers' -- and so on different threads.
/// </summary>
/// <remarks>
/// <para>
/// <b>A port that crosses a thread leaves its object behind.</b> A port is an object of one realm,
/// and a realm is driven by one thread, so a port transferred to a worker cannot be the object the
/// page held: the page's object goes inert, and the worker gets a new one for the same end. What the
/// two objects have in common is this end, which belongs to no realm and which either thread may hold.
/// </para>
/// <para>
/// <b>The end keeps the port's message queue.</b> A message posted to a port waits here until the
/// realm the port is in takes it, one message per task of that realm's event loop, and only once the
/// port is started there. So the messages a port has not delivered yet go wherever the port goes, as
/// HTML's port message queue does, and a port transferred before it was started arrives with them.
/// </para>
/// <para>
/// <b>Every member may be called from any thread.</b> The realm a port is in is its
/// <see cref="IPortHome"/>, which turns "a message is waiting" into a task of that realm's own loop;
/// nothing here runs script.
/// </para>
/// </remarks>
internal sealed class PortEnd
{
    private readonly object _gate = new();
    private readonly LinkedList<PortMessage> _queue = new();
    private PortEnd? _peer;
    private IPortHome? _home;
    private bool _closed;

    private PortEnd()
    {
    }

    /// <summary>Two entangled ends: what one is posted, the other receives.</summary>
    public static (PortEnd First, PortEnd Second) NewPair()
    {
        var first = new PortEnd();
        var second = new PortEnd();
        first._peer = second;
        second._peer = first;
        return (first, second);
    }

    /// <summary>Whether the port this is the end of was closed.</summary>
    public bool IsClosed
    {
        get
        {
            lock (_gate)
                return _closed;
        }
    }

    /// <summary>The messages waiting for the port.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
                return _queue.Count;
        }
    }

    /// <summary>Posts <paramref name="message"/> to the entangled port. Nothing happens when either port is closed.</summary>
    public void Post(PortMessage message)
    {
        PortEnd? peer;
        lock (_gate)
        {
            if (_closed)
                return;

            peer = _peer;
        }

        peer?.Receive(message);
    }

    /// <summary>Adds a message its peer posted to the messages waiting for this end's port.</summary>
    private void Receive(PortMessage message)
    {
        IPortHome? home;
        lock (_gate)
        {
            if (_closed)
                return;

            _queue.AddLast(message);
            home = _home;
        }

        home?.Schedule(this);
    }

    /// <summary>
    /// Puts messages the port had not delivered back in front of the rest, in their order: what a realm
    /// was still holding for the port when the port left it.
    /// </summary>
    public void Requeue(IReadOnlyList<PortMessage> undelivered)
    {
        lock (_gate)
        {
            if (_closed)
                return;

            for (var i = undelivered.Count - 1; i >= 0; i--)
                _queue.AddFirst(undelivered[i]);
        }
    }

    /// <summary>
    /// Takes the next message, for the realm the port is in. Nothing when the port has moved on from
    /// <paramref name="home"/> since it asked, or was closed.
    /// </summary>
    public bool TryTake(IPortHome home, out PortMessage message)
    {
        lock (_gate)
        {
            if (!_closed && ReferenceEquals(_home, home) && _queue.First is { } first)
            {
                message = first.Value;
                _queue.RemoveFirst();
                return true;
            }
        }

        message = default;
        return false;
    }

    /// <summary>The port has arrived in <paramref name="home"/>: what is waiting for it is scheduled there.</summary>
    public void Settle(IPortHome home)
    {
        int waiting;
        lock (_gate)
        {
            if (_closed)
                return;

            _home = home;
            waiting = _queue.Count;
        }

        for (var i = 0; i < waiting; i++)
            home.Schedule(this);
    }

    /// <summary>
    /// The port is leaving <paramref name="home"/> for another realm: messages wait here until it
    /// <see cref="Settle"/>s there.
    /// </summary>
    public void Leave(IPortHome home)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_home, home))
                _home = null;
        }
    }

    /// <summary>
    /// Asks the realm the port is in to deliver what is waiting: when the port is started there, which
    /// is when its messages begin to be dispatched.
    /// </summary>
    public void ScheduleWaiting()
    {
        IPortHome? home;
        int waiting;
        lock (_gate)
        {
            home = _home;
            waiting = _queue.Count;
        }

        if (home is null)
            return;

        for (var i = 0; i < waiting; i++)
            home.Schedule(this);
    }

    /// <summary>
    /// Closes the port: what was waiting for it is dropped, and what its peer posts afterwards goes
    /// nowhere. The peer itself stays open, as a port whose peer was closed does.
    /// </summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _queue.Clear();
            _home = null;
        }
    }
}

/// <summary>The realm a <see cref="PortEnd"/>'s port is in, as seen from any thread.</summary>
internal interface IPortHome
{
    /// <summary>
    /// Queues a task on this realm's event loop that delivers the next message waiting at
    /// <paramref name="end"/>. Called from any thread.
    /// </summary>
    /// <param name="end">The end a message is waiting at.</param>
    void Schedule(PortEnd end);
}

/// <summary>
/// A message on its way between realms: the sender's clone of the data, which belongs to no realm,
/// and the ends of the ports transferred with it.
/// </summary>
internal readonly record struct PortMessage(JsDetachedValue Data, PortEnd[] Ports);
