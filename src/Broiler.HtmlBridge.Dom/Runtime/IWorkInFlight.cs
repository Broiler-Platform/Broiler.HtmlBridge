using System;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// Work a document handed to something running beside it -- a worker -- that will answer through the
/// document's event loop, and how a drain lets that answer arrive before the loop's clock moves on.
/// </summary>
/// <remarks>
/// <para>
/// The event loop's clock is virtual: when nothing is due, a drain moves it straight to the next
/// timer. A worker runs on its own thread in real time, so a page that posted to its worker and set a
/// timeout lost the race every time -- the timeout fired before the worker had answered, or even
/// fetched its script. A drain that may block asks <see cref="HasWorkInFlight"/> before the clock
/// would move (<see cref="HasWorkDueNow"/> false) and waits; one that must not block, a window's
/// render pump, simply does not step until the answer is queued.
/// </para>
/// <para>
/// Internal, and implemented by the bridge alone: the public <c>IDomBridgeRuntime</c> stays as it
/// is, and the drains that use this find it by asking the bridge they hold.
/// </para>
/// </remarks>
internal interface IWorkInFlight
{
    /// <summary>
    /// Whether a worker the document started is still working on what the document handed it, within
    /// the time the document waits for one.
    /// </summary>
    bool HasWorkInFlight { get; }

    /// <summary>
    /// Whether the event loop's next step runs something at the current time rather than moving its
    /// clock on to a later timer.
    /// </summary>
    bool HasWorkDueNow { get; }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for the work in flight to finish. Answers whether it has;
    /// whatever it answered with is then queued on the event loop.
    /// </summary>
    bool AwaitWorkInFlight(TimeSpan timeout);
}
