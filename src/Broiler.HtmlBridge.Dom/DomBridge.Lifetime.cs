using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge;

/// <summary>
/// Session lifetime and deterministic disposal for <see cref="DomBridge"/> (HtmlBridge
/// complexity-reduction roadmap Phase 2, P2.1). The bridge owns per-document runtime resources
/// — a headless layout view, timer/animation queues, event-listener stores, mutation observers,
/// message ports and JavaScript wrapper caches — whose lifetime used to be purely GC-driven.
/// <see cref="Dispose"/> releases all of them so a host can tear a document down explicitly, and
/// the same reset runs on re-attach so re-parsing leaves no state from the prior document.
/// </summary>
/// <remarks>
/// Deferred to later Phase 2 PRs (kept out of scope here so this stays one concern): promoting the
/// registry to a full <c>BrowserDocumentSession</c>, moving <see cref="IDisposable"/> onto
/// <c>IDomBridgeRuntime</c>, and de-globalizing the process-static per-element runtime tables.
/// </remarks>
public sealed partial class DomBridge : IDisposable
{
    private readonly DomBridgeDisposalRegistry _disposal = new();
    private bool _disposed;

    /// <summary>
    /// Releases every per-session resource this bridge owns. Deterministic and idempotent — a
    /// second call is a no-op. After disposal the document/timer entry points
    /// (<see cref="Attach(Broiler.JavaScript.Engine.JSContext, string)"/>,
    /// <see cref="FlushTimers"/>, <see cref="FlushTimerStep"/>, <see cref="FireWindowLoadEvent"/>,
    /// <see cref="HasPendingTimers"/>) throw <see cref="ObjectDisposedException"/>.
    /// </summary>
    /// <remarks>
    /// The bridge does not own the <see cref="Broiler.JavaScript.Engine.JSContext"/> passed to
    /// <see cref="Attach(Broiler.JavaScript.Engine.JSContext, string)"/> — the caller (or the
    /// owning <c>InteractiveSession</c>) disposes it — so this only drops the reference and never
    /// disposes the context. Pending timer/animation callbacks are dropped, never run: disposal
    /// tears down, it does not flush.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
            return;
        // Guard first so any re-entrant callback triggered during teardown short-circuits at the
        // public entry points instead of operating on a half-torn-down bridge.
        _disposed = true;

        // Release the renderer-backed layout view (and its headless container) and drop the
        // per-pass geometry snapshot.
        DisposeLayoutView();

        // Timers, listeners, observers, message ports and viewport-scroll listeners.
        ClearRuntimeSessionState();

        // Computed-style engines/cache and JavaScript wrapper identity caches.
        ResetComputedStyleEngines();
        ClearComputedPropsCache();
        _jsObjects.Clear();
        _documentJSObject = null;
        _windowJSObject = null;
        _visualViewportJSObject = null;

        // The bridge borrows the JS context; only drop the reference.
        _jsContext = null;


        // Drain anything later PRs registered (currently the layout view is released directly
        // above; the registry is the seam future document services release through).
        _disposal.Dispose();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>
    /// Clears the per-session browser-runtime collections — timer/interval/animation-frame and
    /// frame-action queues (and their id counters), the window/target event-listener stores,
    /// mutation observers, active ranges and node iterators, message-port state, sub-window
    /// containers and visual-viewport scroll listeners. Shared by <see cref="Dispose"/> and by
    /// <see cref="ParseHtml"/> so a re-attached/re-parsed document starts from a clean session and
    /// carries no timers, listeners or observers from the previous document.
    /// </summary>
    /// <remarks>
    /// Does not touch the canonical document tree or the process-static per-element runtime tables
    /// (weak, node-keyed — they GC with this session's nodes). The timer maps are concurrent
    /// because JS continuations may register timers on ThreadPool threads;
    /// <c>ConcurrentDictionary.Clear</c> is thread-safe, and a continuation that races to re-add a
    /// timer after the clear is benign — the bridge is being torn down or re-parsed and the entry
    /// points that would run it are gated.
    /// </remarks>
    private void ClearRuntimeSessionState()
    {
        _eventLoop.Clear();
        _smoothScrollTokens.Clear();
        _smoothScrollTokenCounter = 0;

        // Drops the mutation subscription and the pending script list: a re-attached document's
        // injected scripts are its own, and a previous document's already-started flags would
        // suppress the identical script in the next one.
        _scriptInsertion.Clear();

        _eventTargets.Clear();
        _browsingContexts.ResetSession();

        _messaging.ClearPorts();

        _mutations.Clear();
        // Ranges / NodeIterators now self-subscribe to their document's DomDocument.Mutated and are
        // released with the document; the bridge keeps no active-range/iterator registry to clear.
    }
}
