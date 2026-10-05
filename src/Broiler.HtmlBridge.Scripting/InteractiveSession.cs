using Broiler.HtmlBridge.Dom;
using Broiler.HtmlBridge.Scripting;

namespace Broiler.HtmlBridge;

/// <summary>
/// Holds a live JavaScript realm and DOM bridge, allowing the caller to
/// step through pending timer and <c>requestAnimationFrame</c> callbacks
/// one batch at a time.  This enables interactive/animated rendering where
/// intermediate visual states are displayed instead of jumping straight to
/// the final frame.
/// </summary>
public sealed class InteractiveSession : IDisposable
{
    private readonly IDisposable _engineLifetime;
    private readonly IDomBridgeRuntime _bridge;
    private readonly MicroTaskQueue _microTasks;
    private bool _disposed;

    /// <summary>
    /// How far onto the virtual clock queued work counts as due: the load window, and the same span
    /// after the last input the user gave the page (<see cref="DispatchPointer"/>).
    /// </summary>
    private double _horizonMs = DomBridgeRuntimeLimits.AsyncDrainVirtualTimeBudgetMs;

    /// <param name="engineLifetime">
    /// Whatever owns the realm's teardown — a <c>JSContext</c> today. This session disposes it and
    /// does not otherwise touch it, which is why the parameter is typed by what it is used for.
    /// </param>
    internal InteractiveSession(IDisposable engineLifetime, IDomBridgeRuntime bridge, MicroTaskQueue microTasks)
    {
        _engineLifetime = engineLifetime ?? throw new ArgumentNullException(nameof(engineLifetime));
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
    /// Whether the last <see cref="SettleLoadWindow(Action{Func{string}}?, CancellationToken)"/>
    /// exhausted its iteration budget before settling.
    /// </summary>
    public bool AsyncDrainLimitExhausted { get; private set; }

    /// <summary>
    /// Whether queued work is due within the load window — the same bounded question the
    /// non-interactive drains ask (<c>ScriptEngine.DrainAsyncWork</c>,
    /// <c>CaptureService.DrainAsyncWork</c>), against the same
    /// <see cref="DomBridgeRuntimeLimits.AsyncDrainVirtualTimeBudgetMs"/> horizon -- or within that
    /// span after the last input the page was given, which opens a window of its own: a click's
    /// handler that animates a spinner or waits on a request schedules its work after the load
    /// window has long closed.
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
        !_disposed &&
        (_bridge.HasPendingTimersDueBy(_horizonMs) ||
         _bridge is Dom.Runtime.IWorkInFlight { HasWorkInFlight: true });

    /// <summary>
    /// Delivers a user's pointer input to the page's scripts, as the trusted events a browser fires
    /// for it, and runs the microtasks they queue. Answers whether the scripts were given it and
    /// whether they cancelled what it does by default.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The input is hit-tested against the document's own layout, at the viewport the bridge was
    /// given (<see cref="SetViewport"/>), and followed into a frame. A press is <c>pointerdown</c> and
    /// <c>mousedown</c>, and moves focus; a release is <c>pointerup</c>, <c>mouseup</c> and then
    /// <c>click</c> (with a checkbox's, a radio button's or a label's activation), <c>dblclick</c> after
    /// a double click's second, or <c>auxclick</c> for another button. A move is the boundary events of
    /// what the pointer left and reached, then <c>pointermove</c> and <c>mousemove</c>; a leave, the
    /// boundary events alone. Every one has <c>isTrusted</c> true.
    /// </para>
    /// <para>
    /// What the scripts then schedule is due within <see cref="DomBridgeRuntimeLimits.AsyncDrainVirtualTimeBudgetMs"/>
    /// of now on the virtual clock, so <see cref="HasWorkDueInLoadWindow"/> answers for it and a host
    /// steps it as it steps the load window. Read the document with <see cref="CurrentHtml"/> after --
    /// for a move, only when <see cref="RenderVersion"/> says it changed -- and the navigation it may
    /// have asked for with <see cref="TakePendingNavigation"/>.
    /// </para>
    /// </remarks>
    public PointerInputResult DispatchPointer(PointerInput input)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_bridge is not DomBridge bridge)
            return default;

        var result = bridge.DispatchPointerInput(input);
        _microTasks.Drain();
        _horizonMs = Math.Max(_horizonMs, bridge.VirtualNowMs + DomBridgeRuntimeLimits.AsyncDrainVirtualTimeBudgetMs);
        return result;
    }

    /// <summary>
    /// Delivers a user's press or release of a key to the page's scripts, as a browser delivers it.
    /// Answers whether they were given it, whether they cancelled what it does by default, and whether
    /// the page acted on it itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The key goes to the focused element -- or the focused document's body -- as <c>keydown</c> or
    /// <c>keyup</c>, and the page does what the key does by default: Tab and Shift+Tab move focus
    /// through the page and its frames, Enter follows a focused link, clicks a focused button and
    /// submits a text field's form, Space clicks a focused button, checkbox or radio button when it
    /// comes up. A host performs its own default actions -- scrolling, its shortcuts -- only for a key
    /// that was neither cancelled nor <see cref="KeyboardInputResult.Handled"/>.
    /// </para>
    /// <para>
    /// The characters a press types follow it with <see cref="DispatchText"/>; a host's editor's other
    /// changes to a field with <see cref="DispatchEdit"/>. Read the document after, as for
    /// <see cref="DispatchPointer"/>, and the navigation the key may have asked for.
    /// </para>
    /// </remarks>
    public KeyboardInputResult DispatchKey(KeyboardInput input) =>
        DispatchToBridge(bridge => bridge.DispatchKeyboardInput(input));

    /// <summary>
    /// Delivers the characters a key press typed: a <c>keypress</c> for each, and in a focused text
    /// field the edit, with <c>beforeinput</c> and <c>input</c>. A host whose editor has taken the text
    /// in says what the field now holds (<see cref="TextInput.EditedValue"/>), and undoes it when the
    /// page cancelled it.
    /// </summary>
    public KeyboardInputResult DispatchText(TextInput input) =>
        DispatchToBridge(bridge => bridge.DispatchTextInput(input));

    /// <summary>
    /// Delivers a change the host's editor made to the focused text field -- a deletion, a paste --
    /// as <c>beforeinput</c> and <c>input</c>, after which the page holds the value the field shows.
    /// The host undoes it when the page cancelled it.
    /// </summary>
    public KeyboardInputResult DispatchEdit(FieldEdit edit) =>
        DispatchToBridge(bridge => bridge.DispatchFieldEdit(edit));

    /// <summary>
    /// The text field that has focus, where it is and what it holds, for a host that edits it with an
    /// editor of its own; <see langword="null"/> when focus is on anything else.
    /// </summary>
    public FocusedTextField? FocusedTextField =>
        _disposed || _bridge is not DomBridge bridge ? null : bridge.GetFocusedTextField();

    /// <summary>
    /// A number that changes whenever the page's focus moves, so that a host following focus with an
    /// editor of its own asks for <see cref="FocusedTextField"/>, which lays the page out, only then.
    /// </summary>
    public long FocusVersion => _disposed || _bridge is not DomBridge bridge ? 0 : bridge.FocusVersion;

    private KeyboardInputResult DispatchToBridge(Func<DomBridge, KeyboardInputResult> dispatch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_bridge is not DomBridge bridge)
            return default;

        var result = dispatch(bridge);
        _microTasks.Drain();
        _horizonMs = Math.Max(_horizonMs, bridge.VirtualNowMs + DomBridgeRuntimeLimits.AsyncDrainVirtualTimeBudgetMs);
        return result;
    }

    /// <summary>
    /// A number that changes whenever what the page renders may have: its document or one of its
    /// frames' changed, or the bridge's own state the renderer is handed -- inline styles, form values,
    /// style sheets. A host compares it across the input it delivers, a move above all, to serialize
    /// and render the page again only when there is something new to show.
    /// </summary>
    public long RenderVersion => _disposed || _bridge is not DomBridge bridge ? 0 : bridge.RenderVersion;

    /// <summary>
    /// Sets the size, in CSS pixels, the page is shown at: what its scripts read as
    /// <c>innerWidth</c> and <c>innerHeight</c>, and what its layout -- and so the hit test of the
    /// next <see cref="DispatchPointer"/> -- is made at. A host calls it when its window is resized.
    /// </summary>
    public void SetViewport(int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_bridge is DomBridge bridge && width > 0 && height > 0)
        {
            bridge.ViewportWidth = width;
            bridge.ViewportHeight = height;
        }
    }

    /// <summary>
    /// Takes the cross-document navigation the page asked for, clearing it, or returns <c>null</c>
    /// if it asked for none.
    /// </summary>
    /// <remarks>
    /// Call this after the load window has settled, not before: a page's decision to leave is often
    /// made by a script that runs on a timer, so asking straight after the synchronous scripts would
    /// miss it. Call it before disposing the session, too — the request lives on the bridge, and
    /// disposal takes the bridge with it.
    /// <para>
    /// It consumes rather than reports, so a caller that asks again later sees only what the page
    /// asked for since — see <c>IDomBridgeRuntime.TakePendingNavigation</c> for the bug that shape
    /// prevents.
    /// </para>
    /// </remarks>
    public NavigationRequest? TakePendingNavigation() => _disposed ? null : _bridge.TakePendingNavigation();

    /// <summary>
    /// Executes one batch of pending timer and animation-frame callbacks,
    /// drains micro-tasks, and returns the serialised DOM HTML reflecting
    /// the current state.  Returns <c>null</c> if no callbacks were
    /// pending (nothing to do).
    /// </summary>
    public string? Step()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A worker is answering the page and nothing is due before the clock would move on: the step
        // waits for the answer rather than run a later timer first, without blocking the caller --
        // the work in flight counts as pending work (HasWorkDueInLoadWindow), so a host keeps asking.
        if (_bridge is Dom.Runtime.IWorkInFlight { HasWorkInFlight: true, HasWorkDueNow: false })
            return null;

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
    /// <see cref="ScriptEngine.ExecuteInteractive(IReadOnlyList{string}, IReadOnlyList{string}, string, string?)"/>
    /// drains only microtasks, so every timer a page schedules during load is left for the caller to
    /// step; a host stepping them from its UI thread pays each callback batch there, and one batch of
    /// a heavy page is measured in seconds.
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

        Action? batchAction = onIntermediateDocument != null
            ? () => onIntermediateDocument(_bridge.SerializeToHtml)
            : null;

        var status = AsyncDrainOperations.DrainUntilSettled(
            _microTasks,
            _bridge,
            batchAction,
            cancellationToken,
            callerName: "InteractiveSession.SettleLoadWindow");

        AsyncDrainLimitExhausted = status == AsyncDrainStatus.Exhausted;

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
        _engineLifetime.Dispose();
    }
}
