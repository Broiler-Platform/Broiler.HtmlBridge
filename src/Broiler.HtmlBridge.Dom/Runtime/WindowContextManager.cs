using System;
using System.Threading;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// The single owner of a document's browsing-context window-resolution behaviour: canonicalising a
/// window
/// candidate against the sub-window state, resolving the current/owner window, and temporarily switching
/// the global <c>window</c>/<c>document</c>/<c>location</c>/<c>parent</c>/<c>postMessage</c>/<c>self</c>/
/// <c>top</c> bindings into another browsing context while a callback runs. It reads the sub-window
/// identity from the <see cref="BrowsingContextManager"/> and the owner-window map from the shared
/// <see cref="EventTargetRegistry"/> (both held directly), and reaches the realm and the sub-document
/// builder through the narrow <see cref="IWindowContextHost"/> contract.
/// </summary>
/// <remarks>
/// <para>
/// The bridge's window-context methods in <c>DomBridge/Hosts.Window.cs</c> are thin delegators to this
/// owner ("state/behaviour owner, bridge forwards"), reached by <c>MessagingBinding</c> via
/// <c>IMessagingHost</c> and by the sub-document script runner.
/// </para>
/// <para>
/// <b>A window is a <see cref="JsValue"/> here, and the seven globals are saved and restored through the
/// realm.</b> The seven evaluations below are host script by the contract's definition — this repository
/// authored every one of them, they read a global and nothing else, and none is subject to the page's
/// content policy — so they go through <see cref="IJsSource.EvaluateHostScript"/>. Nothing here is
/// engine-typed. The sub-window <em>identity</em> state is <see cref="BrowsingContextManager"/>'s
/// alone, and it holds <see cref="JsValue"/> handles, so the four members of it read below take and
/// answer the handles this file already holds.
/// </para>
/// <para>
/// <b>An absent window is <see cref="JsValue.Missing"/>, never a CLR <see langword="null"/>.</b>
/// Every place that tests for one asks <see cref="JsValue.IsObject"/>, which answers false for a
/// primitive as well as for nothing at all.
/// </para>
/// </remarks>
internal sealed class WindowContextManager(
    IWindowContextHost host,
    BrowsingContextManager browsingContexts,
    EventTargetRegistry eventTargets)
{
    private readonly IWindowContextHost _host = host;
    private readonly BrowsingContextManager _browsingContexts = browsingContexts;
    private readonly EventTargetRegistry _eventTargets = eventTargets;

    public JsValue ResolveCurrentWindow()
    {
        var candidate = _browsingContexts.CurrentWindowOverride;
        if (!candidate.IsObject)
        {
            // `window` on the global object, when the page (or a nested context switch) put one
            // there; the bridge's own top-level window otherwise. A non-object answer is no answer.
            var declared = GetGlobal("window");
            candidate = declared.IsObject ? declared : _host.WindowObject;
        }

        return GetCanonicalWindow(candidate);
    }

    /// <summary>
    /// The sub-window whose browsing context is current, or <c>null</c> when that is the main window
    /// (or there is no window at all). What a caller registering deferred work needs in order to ask
    /// "was this handed to me by a frame?" — the main-window answer being the one it can ignore,
    /// since the main context is the one everything already runs in.
    /// </summary>
    /// <remarks>
    /// Nullable rather than <see cref="JsValue.Missing"/> because its caller
    /// (<c>Dom.Features.TimerBinding</c>) asks the question with
    /// <c>is not { } frameWindow</c>, and a non-nullable struct always matches that pattern.
    /// </remarks>
    public JsValue? ResolveCurrentSubWindow()
    {
        var current = ResolveCurrentWindow();
        return current.IsObject && _browsingContexts.IsSubWindow(current) ? current : null;
    }

    /// <summary>
    /// The canonical window that owns <paramref name="target"/>, or the current window when nothing
    /// recorded an owner for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The map is asked with the handle the caller holds.</b> The two conversions that stood on this
    /// expression existed only to reach an engine-typed accessor over a store that has been keyed on
    /// <see cref="JsValue"/> since the listener stores were re-typed. The accessor takes a handle now,
    /// so the key going in and the window coming back are the values this method already had.
    /// </para>
    /// <para>
    /// <b>The fallback is why a one-window test of this seam cannot fail.</b> A miss — and a
    /// <paramref name="target"/> that is not an object at all — answers <see cref="ResolveCurrentWindow"/>,
    /// so on a page with one window the map's answer and the map's absence are the same window: deleting
    /// <c>_ownerWindows</c> outright would not turn such a test red. A test that would notice has to give
    /// the target an owner that DIFFERS from the current window, which in this bridge means a nested
    /// browsing context — <c>Features/SubWindowBinding.cs</c> files a frame's window as its own owner, and
    /// <c>Features/MessagingBinding.cs</c> files a port transferred into a frame under that frame's window
    /// — and then assert that the listener ran against the frame's document rather than the containing
    /// page's. <c>OwnerWindowRoutingTests</c> is that test.
    /// </para>
    /// </remarks>
    public JsValue ResolveOwnerWindow(JsValue target)
        => target.IsObject && _eventTargets.TryGetOwnerWindow(target, out var ownerWindow)
            ? GetCanonicalWindow(ownerWindow)
            : ResolveCurrentWindow();

    public JsValue GetCanonicalWindow(JsValue candidate)
    {
        if (!candidate.IsObject || candidate == _host.WindowObject)
            return candidate;

        if (_browsingContexts.IsSubWindow(candidate))
            return candidate;

        var realm = _host.Realm;
        foreach (var subWindow in _browsingContexts.SubWindows)
        {
            if (candidate == subWindow)
                return subWindow;

            if (realm is null)
                continue;

            // Two window objects for the same document: same location.href and the same parent. The
            // href is read through the engine's own ToString, because it is a page-visible value and
            // a `location` the page replaced may carry any `href` it likes.
            var candidateHref = HrefOf(realm, candidate);
            var subWindowHref = HrefOf(realm, subWindow);
            if (!string.Equals(candidateHref, subWindowHref, StringComparison.Ordinal))
                continue;

            // Two non-object parents count as the same parent: ObjectOrNone collapses anything that
            // is not an object to one value on both sides.
            if (ObjectOrNone(realm.GetProperty(candidate, "parent"))
                == ObjectOrNone(realm.GetProperty(subWindow, "parent")))
                return subWindow;
        }

        return candidate;

        static JsValue ObjectOrNone(JsValue value) => value.IsObject ? value : JsValue.Missing;

        static string? HrefOf(IJsRealm realm, JsValue window)
        {
            var location = realm.GetProperty(window, "location");
            if (!location.IsObject)
                return null;

            var href = realm.GetProperty(location, "href");
            return href.IsMissing ? null : realm.ToJsString(href);
        }
    }

    /// <summary>
    /// A test of whether <paramref name="window"/> still shows the document it shows now, or
    /// <see langword="null"/> for a window that is not a frame's (the main window's document lives as
    /// long as the session does).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A frame's window is found by its container, and the container outlives its documents: setting
    /// <c>src</c> or <c>srcdoc</c> drops the container's document and window
    /// (<see cref="BrowsingContextManager.RemoveContainerCaches"/>) and builds a new pair, while the old
    /// window keeps its reverse link to the container. Work the old document queued — a promise
    /// reaction, an <c>await</c>, a timer — would otherwise resolve that window to the new document and
    /// run as it: with its <c>document</c>, its cookies and its fetch client. HTML runs no such work;
    /// its document is no longer fully active.
    /// </para>
    /// <para>
    /// So the test is identity, captured now: the same container, still holding the same document
    /// object. Every window a frame's script runs under is minted after its document is recorded
    /// (<c>SubWindowBinding.Build</c> builds the document first), including the re-entrant one the
    /// frame's own scripts run under before the outer build publishes its successor, and both belong
    /// to the same document. A session reset clears the reverse links, so nothing queued before it runs
    /// after it.
    /// </para>
    /// </remarks>
    public Func<bool>? LivenessOf(JsValue window)
    {
        if (!window.IsObject || !_browsingContexts.TryGetSubWindowContainer(window, out var container))
            return null;

        if (!_browsingContexts.TryGetSubDocument(container, out var document))
            return static () => false;

        var browsingContexts = _browsingContexts;
        return () =>
            browsingContexts.TryGetSubWindowContainer(window, out var current) &&
            ReferenceEquals(current, container) &&
            browsingContexts.TryGetSubDocument(container, out var shown) &&
            shown == document;
    }

    /// <summary>
    /// Queues <paramref name="job"/> onto the host's microtask queue as a job of
    /// <paramref name="window"/>'s current document, to run in its window context — or runs nothing
    /// and answers <see langword="false"/> when the host drives no queue.
    /// </summary>
    public bool TryEnqueueJob(JsValue window, Action job)
    {
        if (_host.MicroTasks is not { } queue)
            return false;

        new WindowJobPump(queue, this, window, LivenessOf(window), _host.EngineJobs).Enqueue(job);
        return true;
    }

    public void RunWithWindowContext(JsValue targetWindow, Action callback)
    {
        if (_host.Realm is not { } realm)
        {
            callback();
            return;
        }

        // Undefined rather than "unset": an evaluation below that throws leaves its slot at this
        // value, and the restore then clears the binding.
        var previousWindow = JsValue.Undefined;
        var previousDocument = JsValue.Undefined;
        var previousLocation = JsValue.Undefined;
        var previousParent = JsValue.Undefined;
        var previousPostMessage = JsValue.Undefined;
        var previousSelf = JsValue.Undefined;
        var previousTop = JsValue.Undefined;
        var previousCurrentWindow = _browsingContexts.CurrentWindowOverride;

        // The jobs the callback queues belong to this window: installed for the length of the switch,
        // so a promise or an await created inside captures it, and restored with the globals. A frame
        // always gets one. The main window needs one only inside a frame's switch, where the frame's
        // pump would otherwise take its jobs; anywhere else its jobs stay on the engine's own queue,
        // where they have always run, in the main window's context. The engine sees the pump only
        // through the wrapper its host supplies (IEngineJobs.AsJobQueue); queueMicrotask reads it
        // directly.
        var previousPump = WindowJobPump.Active;
        var previousJobContext = SynchronizationContext.Current;
        var pump = _host.MicroTasks is { } queue &&
                   targetWindow.IsObject &&
                   (_browsingContexts.IsSubWindow(targetWindow) || previousPump is not null)
            ? new WindowJobPump(queue, this, targetWindow, LivenessOf(targetWindow), _host.EngineJobs)
            : null;
        var engineJobs = pump is not null && _host.EngineJobs is { } engine ? engine.AsJobQueue(pump) : null;

        try
        {
            previousWindow = Snapshot(realm, "window");
            previousDocument = Snapshot(realm, "document");
            previousLocation = Snapshot(realm, "location");
            previousParent = Snapshot(realm, "parent");
            previousPostMessage = Snapshot(realm, "postMessage");
            previousSelf = Snapshot(realm, "self");
            previousTop = Snapshot(realm, "top");

            SetGlobal(realm, "window", targetWindow);
            SetGlobal(realm, "document", GetWindowDocument(targetWindow));
            SetGlobal(realm, "location", realm.GetProperty(targetWindow, "location"));
            SetGlobal(realm, "parent", GetWindowParent(targetWindow));
            SetGlobal(realm, "postMessage", realm.GetProperty(targetWindow, "postMessage"));
            SetGlobal(realm, "self", targetWindow);
            SetGlobal(realm, "top", _host.WindowObject.IsObject ? _host.WindowObject : targetWindow);
            _browsingContexts.CurrentWindowOverride = targetWindow;
            if (pump is not null)
                WindowJobPump.Active = pump;
            if (engineJobs is not null)
                SynchronizationContext.SetSynchronizationContext(engineJobs);

            callback();
        }
        finally
        {
            if (engineJobs is not null)
                SynchronizationContext.SetSynchronizationContext(previousJobContext);
            if (pump is not null)
                WindowJobPump.Active = previousPump;

            SetGlobal(realm, "window", previousWindow);
            SetGlobal(realm, "document", previousDocument);
            SetGlobal(realm, "location", previousLocation);
            SetGlobal(realm, "parent", previousParent);
            SetGlobal(realm, "postMessage", previousPostMessage);
            SetGlobal(realm, "self", previousSelf);
            SetGlobal(realm, "top", previousTop);
            _browsingContexts.CurrentWindowOverride = previousCurrentWindow;
        }
    }

    /// <summary>
    /// The binding's current value, or <c>undefined</c> when the name is not declared at all.
    /// </summary>
    /// <remarks>
    /// A <c>typeof</c> guard rather than a plain read, because a bare name that was never declared is
    /// a <c>ReferenceError</c> and this runs before the switch has declared anything.
    /// </remarks>
    private static JsValue Snapshot(IJsRealm realm, string name) =>
        realm.EvaluateHostScript(
            $"typeof {name} === 'undefined' ? undefined : {name}", $"broiler:window-context-save:{name}");

    private static void SetGlobal(IJsRealm realm, string name, JsValue value) =>
        realm.SetProperty(realm.Global, name, value);

    private JsValue GetGlobal(string name) =>
        _host.Realm is { } realm ? realm.GetProperty(realm.Global, name) : JsValue.Missing;

    public JsValue GetWindowDocument(JsValue targetWindow)
    {
        if (IsMainWindow(targetWindow))
            return _host.MainDocumentOrUndefined;

        return _browsingContexts.TryGetSubWindowContainer(targetWindow, out var containerElement)
            ? _host.GetOrCreateSubDocument(containerElement)
            : JsValue.Undefined;
    }

    public JsValue GetWindowParent(JsValue targetWindow)
    {
        // The main window's parent is itself, and under an engine whose global object *is* the window
        // that is what `this` evaluates to at the top level — asked for directly rather than
        // compiled for.
        if (IsMainWindow(targetWindow))
            return _host.Realm is { } mainRealm ? mainRealm.Global : targetWindow;

        if (_host.Realm is { } realm && realm.GetProperty(targetWindow, "parent") is { IsMissing: false } parent)
            return parent;

        return _host.WindowObject.IsObject ? _host.WindowObject : targetWindow;
    }

    private bool IsMainWindow(JsValue window) => window.IsObject && window == _host.WindowObject;
}
