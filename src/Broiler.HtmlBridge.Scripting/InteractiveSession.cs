using Broiler.HtmlBridge.Dom;
using Broiler.HtmlBridge.Scripting;
using Broiler.JavaScript.Engine;

namespace Broiler.HtmlBridge;

/// <summary>
/// Holds a live JavaScript context and DOM bridge, allowing the caller to
/// step through pending timer and <c>requestAnimationFrame</c> callbacks
/// one batch at a time.  This enables interactive/animated rendering where
/// intermediate visual states are displayed instead of jumping straight to
/// the final frame.
/// </summary>
public sealed class InteractiveSession : IDisposable
{
    private readonly JSContext _context;
    private readonly IDomBridgeRuntime _bridge;
    private readonly MicroTaskQueue _microTasks;
    private bool _disposed;

    internal InteractiveSession(JSContext context, IDomBridgeRuntime bridge, MicroTaskQueue microTasks)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _microTasks = microTasks ?? throw new ArgumentNullException(nameof(microTasks));
    }

    /// <summary>
    /// Returns <c>true</c> when there are queued <c>setTimeout</c>,
    /// <c>setInterval</c>, or <c>requestAnimationFrame</c> callbacks
    /// waiting to execute.
    /// </summary>
    public bool HasPendingWork => !_disposed && _bridge.HasPendingTimers;

    /// <summary>
    /// Whether queued work is due within the load window — the same bounded question the
    /// non-interactive drains ask (<c>ScriptEngine.DrainAsyncWork</c>,
    /// <c>CaptureService.DrainAsyncWork</c>), against the same
    /// <see cref="DomBridgeRuntimeLimits.AsyncDrainVirtualTimeBudgetMs"/> horizon.
    /// </summary>
    /// <remarks>
    /// This is the predicate a render pump must drive itself on. <see cref="HasPendingWork"/>
    /// answers "are any callbacks queued at all", which on a page holding a <c>setInterval</c> —
    /// google.com among them — is <c>true</c> forever by design: an interval always has a next
    /// tick. A pump that steps while that is true never stops, and since each step runs a callback
    /// batch and re-serialises the document, it does so at whatever a batch happens to cost.
    /// Work scheduled past the horizon is later, not stuck, and the page is loaded without it.
    /// </remarks>
    public bool HasWorkDueInLoadWindow =>
        !_disposed && _bridge.HasPendingTimersDueBy(DomBridgeRuntimeLimits.AsyncDrainVirtualTimeBudgetMs);

    /// <summary>
    /// Executes one batch of pending timer and animation-frame callbacks,
    /// drains micro-tasks, and returns the serialised DOM HTML reflecting
    /// the current state.  Returns <c>null</c> if no callbacks were
    /// pending (nothing to do).
    /// </summary>
    public string? Step()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_bridge.FlushTimerStep())
            return null;

        _microTasks.Drain();
        return _bridge.SerializeToHtml();
    }

    /// <summary>
    /// Runs the load window to a fixed point and returns the resulting document, leaving only
    /// work that is genuinely later — the settled page a caller can render once.
    /// </summary>
    /// <remarks>
    /// The same loop as <c>ScriptEngine.DrainAsyncWork</c> and <c>CaptureService.DrainAsyncWork</c>:
    /// microtasks first, then one timer batch, bounded by
    /// <see cref="DomBridgeRuntimeLimits.AsyncDrainVirtualTimeBudgetMs"/> on the virtual clock and
    /// <see cref="DomBridgeRuntimeLimits.AsyncDrainIterationLimit"/> on the iteration count — the
    /// backstop for work that regenerates at the current instant and never lets the clock move.
    /// <para>
    /// It exists so a host can settle a page off the thread it paints on.
    /// <see cref="ScriptEngine.ExecuteInteractive"/> drains only microtasks, so every timer a page
    /// schedules during load is left for the caller to step; a host stepping them from its UI
    /// thread pays each callback batch there, and one batch of a heavy page is measured in seconds.
    /// </para>
    /// </remarks>
    public string SettleLoadWindow(CancellationToken cancellationToken = default) =>
        SettleLoadWindow(onIntermediateDocument: null, cancellationToken);

    /// <summary>
    /// As <see cref="SettleLoadWindow(CancellationToken)"/>, reporting the document after every
    /// batch so a host can paint the load window as it runs instead of only its final state.
    /// </summary>
    /// <param name="onIntermediateDocument">
    /// Called after each batch with a thunk that serialises the current document. A settle that
    /// reports nothing is the whole reason a page which animates while loading — Acid3 counting its
    /// score up, one test per <c>setTimeout</c> — arrives on screen already finished: the batches
    /// all ran before the first paint. The document is passed as a thunk rather than a string
    /// because serialising is not free and a host that is still painting the previous frame wants
    /// to skip this one; it pays only for the frames it actually shows.
    /// </param>
    /// <param name="cancellationToken">Stops the settle; the caller's Stop, in a browser.</param>
    /// <remarks>
    /// The callback runs on the settling thread, between batches, so it must not run the page's
    /// script or touch the DOM — read the document it is handed and return.
    /// </remarks>
    public string SettleLoadWindow(Action<Func<string>>? onIntermediateDocument, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        for (var iteration = 0; iteration < DomBridgeRuntimeLimits.AsyncDrainIterationLimit; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hadWork = false;

            if (_microTasks.Count > 0)
            {
                _microTasks.Drain();
                hadWork = true;
            }

            if (_bridge.HasPendingTimersDueBy(DomBridgeRuntimeLimits.AsyncDrainVirtualTimeBudgetMs))
            {
                _bridge.FlushTimerStep();
                hadWork = true;
            }

            if (!hadWork)
                break;

            onIntermediateDocument?.Invoke(_bridge.SerializeToHtml);
        }

        return _bridge.SerializeToHtml();
    }

    /// <summary>
    /// Executes one pending callback batch and returns the live canonical
    /// document for direct rendering.
    /// </summary>
    public Broiler.Dom.DomDocument? StepDocument()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_bridge.FlushTimerStep())
            return null;

        _microTasks.Drain();
        return _bridge.GetRenderDocument();
    }

    /// <summary>
    /// Serialises the current DOM state without executing any callbacks.
    /// </summary>
    public string CurrentHtml()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _bridge.SerializeToHtml();
    }

    public Broiler.Dom.DomDocument CurrentDocument()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _bridge.GetRenderDocument();
    }

    /// <summary>
    /// Flushes all remaining timers (like the non-interactive path) and
    /// returns the final serialised HTML.
    /// </summary>
    public string Complete()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _bridge.FlushTimers();
        _microTasks.Drain();
        return _bridge.SerializeToHtml();
    }

    /// <summary>
    /// Disposes the session's private event-loop/context lifetime: the DOM bridge
    /// (its timers, listeners, observers and layout view) is torn down first, then the
    /// JS context. Deterministic and idempotent — a second call is a no-op.
    /// </summary>
    /// <remarks>
    /// The bridge owns the browser event loop; <see cref="DomBridge.Dispose"/> only drops its
    /// borrowed reference to the context, so the session must dispose the context itself. Tear the
    /// bridge down before the context so any re-entrant teardown still sees a live realm.
    /// <see cref="IDomBridgeRuntime"/> is not itself <see cref="IDisposable"/>, so the concrete
    /// bridge is disposed through a cast (a null-safe no-op for a hypothetical non-disposable runtime).
    /// </remarks>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        (_bridge as IDisposable)?.Dispose();
        _context.Dispose();
    }
}
