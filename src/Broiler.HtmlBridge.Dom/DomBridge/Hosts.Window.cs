using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Broiler.Dom;
using Broiler.HtmlBridge.Core.Diagnostics;
using Broiler.HtmlBridge.Dom;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JSeal;
using Broiler.HtmlBridge.Logging;
using Broiler.HtmlBridge.Scripting;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

// Explicit ILocationHost implementation for the LocationBinding feature module, following the same
// shape as DomBridge.WindowEventTargetHost: the module reaches a named contract rather than a bridge
// private, and the public surface gains only the PendingNavigation the runtime interface declares.
//
// The dispatch below is a plain forward now: window dispatch takes the handle the module built, so
// there is no conversion left here to describe and no engine type named on either side of this seam.
public sealed partial class DomBridge : Dom.Features.ILocationHost
{
    private NavigationRequest? _pendingNavigation;

    /// <inheritdoc />
    public NavigationRequest? TakePendingNavigation()
    {
        var pending = _pendingNavigation;
        _pendingNavigation = null;
        return pending;
    }

    void Dom.Features.ILocationHost.DispatchWindowEvent(JsValue evt)
        => DispatchWindowEvent(evt);

    void Dom.Features.ILocationHost.RequestNavigation(NavigationRequest request)
        => RequestNavigation(request);

    /// <summary>
    /// Records where the page asked to go. Nothing is loaded here — see
    /// <see cref="NavigationRequest"/> for why the decision belongs to the host.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Last request wins. A browser starts navigating on the first assignment and supersedes it on
    /// the second, landing on the last one the script asked for before it stopped running; keeping
    /// the first instead would follow a target the page had already changed its mind about.
    /// </para>
    /// <para>
    /// A request that names no initiator is stamped with the document whose script is running — a
    /// frame's, when a frame's script navigates the top window — so the host can decide SameSite for
    /// the navigation from the document that actually started it.
    /// </para>
    /// </remarks>
    private void RequestNavigation(NavigationRequest request)
    {
        if (_pendingNavigation is { } superseded)
        {
            RenderLogger.LogDebug(LogCategory.JavaScript, "DomBridge.location",
                $"{superseded.Url} superseded by {request.Url} before the document settled; the later request is the one that stands");
        }

        _pendingNavigation = request.Initiator is null
            ? request with { Initiator = CurrentScriptDocumentContext() }
            : request;
    }
}

// Explicit IMatchMediaHost implementation for the MatchMediaBinding feature module:
// the bridge exposes only the live viewport dimensions, via explicit interface members so the
// module never reaches an arbitrary bridge private field and the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IMatchMediaHost
{
    int Dom.Features.IMatchMediaHost.ViewportWidth => _viewportWidth;

    int Dom.Features.IMatchMediaHost.ViewportHeight => _viewportHeight;
}

// Explicit IWindowDocumentMiscHost implementation for the WindowDocumentMiscBinding feature module:
// the bridge exposes the current page URL and the visual-viewport scale setter via explicit
// interface members, so the module never reaches an arbitrary bridge private field and the public
// surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IWindowDocumentMiscHost
{
    void Dom.Features.IWindowDocumentMiscHost.SetVisualViewportScale(double scale)
        => SetVisualViewportScale(scale);
}

// Explicit IWindowEventTargetHost implementation for the WindowEventTargetBinding feature module:
// the bridge exposes the window's per-type listener store (EventTargetRegistry) and the
// window-scoped dispatch via explicit interface members, so the module never reaches an arbitrary
// bridge private field and the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IWindowEventTargetHost
{
    List<EventListenerRegistration> Dom.Features.IWindowEventTargetHost.WindowListenersForAdd(string type)
        => _eventTargets.WindowListenersForAdd(type);

    bool Dom.Features.IWindowEventTargetHost.TryGetWindowListeners(string type, out List<EventListenerRegistration> listeners)
        => _eventTargets.TryGetWindowListeners(type, out listeners);

    JsValue Dom.Features.IWindowEventTargetHost.DispatchWindowEvent(JsValue evt)
        => JsValue.Boolean(DispatchWindowEvent(evt));
}

// Explicit IWindowScrollHost implementation for the WindowScrollBinding feature module:
// the bridge exposes the document (scrolling) element, the JS scroll-argument parser and the scroll
// primitive via explicit interface members, so the module never reaches an arbitrary bridge private
// field and the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IWindowScrollHost
{
    // One reading, not two: the JSEAL-framed argument list is read by the bridge's own ISubWindowHost
    // member, which performs exactly what the engine-framed GetScrollArguments this replaces
    // performed — an options object wins over positional coordinates, a nullish member leaves its
    // axis alone, and both coercions are the realm's, as DoubleValue and ToString() were the
    // engine's. Forwarding is how the window and sub-window contracts share that one reading rather
    // than each carrying a copy of it.
    (double? Left, double? Top, string? Behavior) Dom.Features.IWindowScrollHost.GetScrollArguments(
        ReadOnlySpan<JsValue> arguments)
        => ((Dom.Features.ISubWindowHost)this).GetScrollArguments(arguments);

    void Dom.Features.IWindowScrollHost.SetElementScrollOffsetsWithBehavior(
        DomElement element, double? left, double? top, bool relative, bool clamp, string? behavior)
        => SetElementScrollOffsetsWithBehavior(element, left, top, relative, clamp, behavior);
}

// Explicit IVisualViewportEventTargetHost implementation for the VisualViewportEventTargetBinding
// feature module: the bridge exposes the visual-viewport scroll listener store (from the
// EventTargetRegistry) via explicit interface members, so the module never reaches an arbitrary
// bridge private field and the public surface is unchanged.
//
// The contract is spelled in JSEAL handles, and so is the store behind it: Runtime/EventTargetRegistry.cs
// keeps the handle a listener arrived as, and the scroll dispatch in LayoutMetrics.Scrolling.cs invokes
// that handle through the realm, so nothing in the bridge between the page's addEventListener argument
// and that call names an engine type.
//
// The IsFunction test is a decision rather than a change of type, and it stays.
// VisualViewportEventTargetBinding.IsScrollListener makes the same test first and
// is this contract's only caller, so it cannot fail today; but the dispatcher invokes whatever the
// store holds, and with every element kind Function, JsValue's kind-then-reference equality asks
// List.Contains and List.Remove exactly what they asked of the engine's functions.
public sealed partial class DomBridge : Dom.Features.IVisualViewportEventTargetHost
{
    void Dom.Features.IVisualViewportEventTargetHost.AddVisualViewportScrollListener(JsValue listener)
    {
        if (listener.IsFunction)
            _eventTargets.AddVisualViewportScrollListener(listener);
    }

    void Dom.Features.IVisualViewportEventTargetHost.RemoveVisualViewportScrollListener(JsValue listener)
    {
        if (listener.IsFunction)
            _eventTargets.RemoveVisualViewportScrollListener(listener);
    }
}

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IWindowContextHost"/>, the contract the
/// <see cref="WindowContextManager"/> owner consumes. Explicit interface members, so these realm seams do not widen the public
/// <c>DomBridge</c> surface — and <see cref="DomBridge.Realm"/> is internal, so an implicit
/// implementation of <see cref="IWindowContextHost.Realm"/> would not compile.
/// </summary>
/// <remarks>
/// Nothing here converts. The window and document are the bridge's roots, which are the handles the
/// realm minted, and the sub-document builder answers a handle of its own, so the window the manager
/// compares against is the same instance by construction rather than by a cast.
/// </remarks>
public sealed partial class DomBridge : IWindowContextHost
{
    IJsRealm? IWindowContextHost.Realm => _realm;

    JsValue IWindowContextHost.WindowObject => WindowHandle;

    // Undefined rather than the Missing the root holds, as the member's name says: its one consumer,
    // WindowContextManager.GetWindowDocument, answers undefined on its other branch too.
    JsValue IWindowContextHost.MainDocumentOrUndefined =>
        DocumentHandle.IsMissing ? JsValue.Undefined : DocumentHandle;

    Broiler.HtmlBridge.Scripting.MicroTaskQueue? IWindowContextHost.MicroTasks => MicroTaskQueue;

    Dom.Runtime.IEngineJobs? IWindowContextHost.EngineJobs => EngineJobs;

    /// <summary>
    /// Set by the script engine that drives this bridge: how its engine takes a window's job pump as
    /// its job queue, and how a job joins the engine's own queue behind the running script's
    /// (<see cref="Dom.Runtime.IEngineJobs"/>). The bridge names no engine, so without it a frame's
    /// promise jobs run wherever the engine runs them; <c>queueMicrotask</c> is attributed either way.
    /// </summary>
    internal Dom.Runtime.IEngineJobs? EngineJobs { get; set; }

    /// <summary>
    /// The host's microtask queue — the one its <see cref="TaskCheckpointCallback"/> drains — set by
    /// the script engine that drives this bridge. With it, the jobs a frame's script queues run in the
    /// frame's window context (see <see cref="Dom.Runtime.WindowJobPump"/>); without it they run
    /// wherever the engine drains them, which is the top document's context.
    /// </summary>
    internal Broiler.HtmlBridge.Scripting.MicroTaskQueue? MicroTaskQueue { get; set; }

    /// <summary>
    /// Queues <paramref name="job"/> for the browsing context whose script is running: through that
    /// window's job pump when a window context switch is in progress, as the page's otherwise. What
    /// <c>queueMicrotask</c> queues through, so its callback runs as the document that queued it, and
    /// in its place among the promise jobs already queued: behind them on the engine's own queue while
    /// script is running (<paramref name="engine"/>), on <paramref name="queue"/> when none is.
    /// </summary>
    internal static void QueueMicrotask(
        Broiler.HtmlBridge.Scripting.MicroTaskQueue queue,
        Dom.Runtime.IEngineJobs? engine,
        Action job)
    {
        if (Dom.Runtime.WindowJobPump.Active is { } pump)
            pump.Queue(job);
        else
            Dom.Runtime.OnceJob.Queue(queue, engine, job);
    }
}

/// <summary>
/// Thin bridge delegators for the browsing-context window-resolution behaviour, which lives in the
/// single <see cref="Broiler.HtmlBridge.Dom.Runtime.WindowContextManager"/> owner; that owner reads
/// the sub-window state from <c>BrowsingContextManager</c>. The
/// <see cref="Broiler.HtmlBridge.Dom.Features.MessagingBinding"/> reaches these through the
/// <see cref="Broiler.HtmlBridge.Dom.Features.IMessagingHost"/> contract, and the sub-document script
/// runner calls <c>RunWithWindowContext</c> directly.
/// </summary>
/// <remarks>
/// Forwards to <see cref="Broiler.HtmlBridge.Dom.Runtime.WindowContextManager"/>; every caller
/// already holds a <see cref="JsValue"/>.
/// </remarks>
public sealed partial class DomBridge
{
    private JsValue ResolveCurrentWindow() => WindowOrNull(_windowContext.ResolveCurrentWindow());

    private JsValue ResolveOwnerWindow(JsValue target) =>
        WindowOrNull(_windowContext.ResolveOwnerWindow(target));

    private JsValue GetCanonicalWindow(JsValue candidate) => _windowContext.GetCanonicalWindow(candidate);

    private void RunWithWindowContext(JsValue targetWindow, Action callback) =>
        _windowContext.RunWithWindowContext(targetWindow, callback);

    private JsValue GetWindowDocument(JsValue targetWindow) => _windowContext.GetWindowDocument(targetWindow);

    private JsValue GetWindowParent(JsValue targetWindow) => _windowContext.GetWindowParent(targetWindow);
}

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IMessagingHost"/>, the narrow contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.MessagingBinding"/> feature module consumes.
/// Each member is an explicit interface implementation, so these seams do not widen the public
/// <c>DomBridge</c> surface. They forward to the browsing-context machinery: top-window dispatch,
/// frame-action queueing, and the window resolution and window-context switch that the delegators
/// above hand to <c>WindowContextManager</c>.
/// </summary>
/// <remarks>
/// Nothing here converts. The contract speaks in <see cref="JsValue"/> handles and the window is the
/// bridge's root, which is one. A window that does not exist yet crosses as JavaScript <c>null</c>, not
/// as the <see cref="JsValue.Missing"/> the root holds, and that is kept rather than collapsed: the
/// module asks <c>IsObject</c> in most places, which reads the two alike, but it also compares a
/// target window against this member with handle equality, and there null and Missing are different
/// answers.
/// </remarks>
public sealed partial class DomBridge : IMessagingHost
{
    JsValue IMessagingHost.WindowObject =>
        WindowHandle.IsMissing ? JsValue.Null : WindowHandle;

    string IMessagingHost.PageOrigin => _pageOrigin;

    // Both answer JsValue.Null for "no window" themselves, so this is a plain delegation.
    JsValue IMessagingHost.ResolveCurrentWindow() => ResolveCurrentWindow();

    JsValue IMessagingHost.ResolveOwnerWindow(JsValue target) => ResolveOwnerWindow(target);

    void IMessagingHost.RunWithWindowContext(JsValue targetWindow, Action callback) =>
        RunWithWindowContext(targetWindow, callback);

    void IMessagingHost.QueueFrameAction(Action callback) => QueueFrameAction(callback);

    void IMessagingHost.DispatchWindowEvent(JsValue evt) => DispatchWindowEvent(evt);

    string? IMessagingHost.FrameWindowOrigin(JsValue window) =>
        window.IsObject && _browsingContexts.TryGetSubWindowContainer(window, out var container)
            ? FrameDocumentContext(container).Origin.ToString()
            : null;

    bool IMessagingHost.AreWindowsCrossOrigin(JsValue first, JsValue second) =>
        AreCrossOriginForAccess(DocumentContextOfWindow(first), DocumentContextOfWindow(second));

    /// <summary>The request context of the document <paramref name="window"/> shows: a frame's, or the top document's.</summary>
    private Broiler.Net.Http.DocumentRequestContext DocumentContextOfWindow(JsValue window) =>
        window.IsObject && _browsingContexts.TryGetSubWindowContainer(window, out var container)
            ? FrameDocumentContext(container)
            : TopDocumentContext;
}

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IWorkerHost"/> — the narrow contract the
/// <see cref="Broiler.HtmlBridge.Dom.Features.WorkerBinding"/> feature module consumes, in the same
/// shape as the <see cref="IMessagingHost"/> implementation above. Explicit interface implementations, so the seams do
/// not widen the public <c>DomBridge</c> surface.
/// </summary>
/// <remarks>
/// Script resolution is a file-system policy and names no JavaScript type; the only member that used
/// to was the script context, and it is the realm now.
/// </remarks>
public sealed partial class DomBridge : IWorkerHost
{
    /// <summary>
    /// The bridge's realm, or <see langword="null"/> while there is none.
    /// </summary>
    /// <remarks>
    /// The field itself, not the <c>Realm</c> property: that property throws when the bridge is not
    /// attached, and this seam's whole job is to let a worker ask the question and be told "no"
    /// without an exception — the worker thread that asks may be racing the bridge's teardown.
    /// </remarks>
    IJsRealm? IWorkerHost.Realm => _realm;

    /// <summary>
    /// Queued on the page's <c>BrowserEventLoop</c>, whose frame-action store is a
    /// <c>ConcurrentDictionary</c> — so this is safe to call from a worker thread, which is the
    /// whole point of the seam. Deliberately does <em>not</em> go through the disposed guard: a
    /// worker thread can be mid-post while the bridge tears down, and throwing
    /// <see cref="ObjectDisposedException"/> onto that thread would surface as a worker crash rather
    /// than the no-op it should be.
    /// </summary>
    void IWorkerHost.QueueFrameAction(Action callback)
    {
        if (_disposed)
            return;

        try
        {
            QueueFrameAction(callback);
        }
        catch (ObjectDisposedException)
        {
            // Raced with disposal; the message simply does not arrive, which is correct.
        }
    }

    /// <summary>
    /// Resolves a worker script against <paramref name="baseDirectory"/> when one is given (the
    /// <c>importScripts</c> case), otherwise against the page's local base path, then as given.
    /// </summary>
    /// <remarks>
    /// Only <c>file</c>-shaped specifiers are resolved. A worker script that would have to be
    /// fetched over the network returns <see langword="null"/> here — surfacing as an <c>error</c>
    /// event for <c>new Worker()</c>, and as a <c>NetworkError</c> for <c>importScripts</c> — rather
    /// than blocking a render on a request this host has no policy for.
    /// </remarks>
    WorkerScript? IWorkerHost.ResolveWorkerScript(string specifier, string? baseDirectory)
    {
        // Every worker script is read from disk, so only a document that may read local files gets
        // one (LocalFileAccess): an http(s) page naming file:, UNC or process-relative paths gets the
        // error a script it could not fetch gives.
        if (!LocalFileAccess.AllowedFor(CurrentScriptDocumentContext().DocumentUrl))
            return null;

        try
        {
            if (Uri.TryCreate(specifier, UriKind.Absolute, out var absolute))
            {
                if (!absolute.IsFile)
                    return null;

                return Read(absolute.LocalPath);
            }

            // The worker's own directory first when there is one: importScripts resolves against the
            // worker's script URL, not the document's.
            if (!string.IsNullOrEmpty(baseDirectory))
            {
                var relative = Path.Combine(baseDirectory, specifier);
                if (File.Exists(relative))
                    return Read(relative);
            }

            var pageBase = _resources.LocalBasePath;
            if (!string.IsNullOrEmpty(pageBase))
            {
                var candidate = Path.Combine(pageBase, specifier);
                if (File.Exists(candidate))
                    return Read(candidate);
            }

            return File.Exists(specifier) ? Read(specifier) : null;
        }
        catch (Exception ex)
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.ResolveWorkerScript",
                $"Could not read worker script '{specifier}': {ex.Message}", ex);
            return null;
        }

        static WorkerScript? Read(string path) =>
            File.Exists(path)
                ? new WorkerScript(File.ReadAllText(path), Path.GetDirectoryName(Path.GetFullPath(path)))
                : null;
    }
}

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IFetchHost"/>, the narrow contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.FetchBinding"/> feature module consumes.
/// Every member is an explicit interface implementation, so none of them widens the public
/// <c>DomBridge</c> surface.
/// </summary>
public sealed partial class DomBridge : IFetchHost
{
    /// <summary>
    /// The entry list of a <c>&lt;form&gt;</c> wrapper, or <see langword="null"/> for anything else —
    /// what <c>new FormData(form)</c> collects.
    /// </summary>
    /// <remarks>
    /// The <c>IsObject</c> test and the lookup answer the same question now: a handle that is not an
    /// object is not in the wrapper map either. The test is kept because collapsing the ten redundant
    /// guards this re-typing left across the assembly is a separate change, not because a primitive
    /// would reach anything that minds.
    /// </remarks>
    IReadOnlyList<KeyValuePair<string, string>>? IFetchHost.FormEntriesFor(JsValue candidate) =>
        candidate.IsObject &&
        FindDomNodeByJSObject(candidate) is Broiler.Dom.DomElement element &&
        string.Equals(element.TagName, "form", StringComparison.OrdinalIgnoreCase)
            ? BuildFormEntryList(element)
            : null;

    JsValue IFetchHost.StreamOverText(string text) => _streams.StreamOverText(text);

    JsValue IFetchHost.StreamOverTextObserved(string text, System.Action onDisturbed) =>
        _streams.StreamOverTextObserved(text, onDisturbed);

    bool IFetchHost.IsStreamLocked(JsValue stream) => _streams.IsStreamLocked(stream);

    JsValue IFetchHost.CreateBlob(byte[] bytes, string contentType) =>
        _blobs.CreateBlobFromBytes(Realm, bytes, contentType);

    Broiler.Net.Http.DocumentRequestContext IFetchHost.FetchClient => CurrentScriptDocumentContext();

    string IFetchHost.FetchBaseUrl => CurrentScriptBaseUrl();

    (byte[] Bytes, string Type)? IFetchHost.BlobContentOf(JsValue candidate) => _blobs.ContentOf(candidate);
}

// Explicit IScriptInsertionHost implementation for the ScriptInsertionRunner (the runtime owner of
// script-inserted <script> execution): the bridge exposes the watched document, the page URL and
// policy, JS evaluation, the event-loop queue and element event dispatch via explicit interface
// members, so the runner never reaches an arbitrary bridge private field and the public surface is
// unchanged.
//
// The JavaScript vocabulary here is JSEAL's. A script element's program text is a CLASSIC SCRIPT,
// so it is evaluated through IJsSource.EvaluateClassicScript.
//
// It deliberately does NOT go through the eval-gated member. The argument for that would be
// provenance -- the text is the page's and not this repository's -- which is true and is not what
// decides the member. What decides it is which Content-Security-Policy directive governs the source.
// A script element is script-src's -- per script, satisfied by 'unsafe-inline', a nonce or a hash --
// and the decision has already been taken, by ScriptInsertionRunner, before this is called.
// 'unsafe-eval' governs eval and new Function and has nothing to say about this text, so routing it
// through the eval-gated member would refuse, on a realm narrowed by a restrictive policy, a script
// every browser runs.
//
// The dispatch call is not engine-typed either: FireSimpleEvent builds its event through the realm,
// and Features/EventDispatchBinding.cs's DispatchEventOnElement takes that handle as it is.
public sealed partial class DomBridge : Dom.Runtime.IScriptInsertionHost
{
    DomDocument Dom.Runtime.IScriptInsertionHost.Document => _document;

    bool Dom.Runtime.IScriptInsertionHost.HasRealm => _realm is not null;

    ContentSecurityPolicy? Dom.Runtime.IScriptInsertionHost.Csp => Csp;

    // Inserted scripts run only when connected to the watched (top) document, so they are fetched
    // as that document's.
    ScriptFetchContext? Dom.Runtime.IScriptInsertionHost.ScriptFetch => ScriptFetchFor(TopDocumentContext);

    bool Dom.Runtime.IScriptInsertionHost.MutationDeliverySuppressed => _mutationDeliverySuppressionDepth > 0;

    void Dom.Runtime.IScriptInsertionHost.QueueTask(Action task) => _eventLoop.QueueTask(task);

    void Dom.Runtime.IScriptInsertionHost.EvaluateScript(DomElement script, string source, string label)
    {
        // A script body is a turn too, and the one most likely to be the long pole at load; see
        // JsEntryTrace. Inactive by default.
        using var turn = JsEntryTrace.Enter(JsEntryKind.Script, label);

        // The script is document.currentScript while it runs, and the one before it — the inserting
        // script's, for an inline script inserted from another — is restored after, even on a throw.
        var previous = RunningInsertedScript;
        RunningInsertedScript = script;
        try
        {
            _realm?.EvaluateClassicScript(source, label);
        }
        finally
        {
            RunningInsertedScript = previous;
        }
    }

    string Dom.Runtime.IScriptInsertionHost.TextContentOf(DomElement element) => element.TextContent;

    void Dom.Runtime.IScriptInsertionHost.FireSimpleEvent(DomElement target, string type)
    {
        if (_realm is not { } realm)
            return;

        try
        {
            // Materialise the wrapper first: that is what compiles an inline on* attribute into a
            // listener, so <script src=… onload=…> is covered as well as an assigned .onload and an
            // addEventListener registration — all three land on the one dispatch path.
            WrapNode(target);
            var evt = realm.NewObject();
            realm.DefineValue(evt, "type", JsValue.String(type));
            realm.DefineValue(evt, "bubbles", JsValue.False);
            _eventDispatch.DispatchEventOnElement(target, evt);
        }
        catch (Exception ex)
        {
            RenderLogger.LogError(LogCategory.JavaScript, "DomBridge.FireScriptEvent",
                $"Error firing '{type}' at a script-inserted <script>: {ex.Message}", ex);
        }
    }
}

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IMutationObserverHost"/>, the narrow
/// contract the extracted <see cref="Broiler.HtmlBridge.Dom.Features.MutationObserverBinding"/>
/// feature module consumes. Explicit interface members, so these seams do not widen the public
/// <c>DomBridge</c> surface.
/// </summary>
/// <remarks>
/// <c>Runtime/JsObjectRegistry</c> keys on <see cref="JsValue.ObjectIdentity"/>. A record's
/// <c>target</c> is the same wrapper instance a page already holds because it is the same handle.
/// </remarks>
public sealed partial class DomBridge : IMutationObserverHost
{
    DomNode? IMutationObserverHost.FindNode(JsValue wrapper) => FindDomNodeByJSObject(wrapper);

    // Mutation-observer delivery is driven off canonical DomDocument.Mutated (the observer binding
    // subscribes per observed document). The bridge suppresses delivery while it mutates the live
    // tree as an implementation detail — serialize/render attribute bakes, document re-parse — so
    // those mutations are not delivered to script observers and, critically, cannot re-enter script
    // synchronously mid-serialize. Depth-counted so nested suppressed regions compose.
    private int _mutationDeliverySuppressionDepth;

    bool IMutationObserverHost.MutationDeliverySuppressed => _mutationDeliverySuppressionDepth > 0;

    /// <summary>
    /// Opens a scope in which script-observable mutation-record delivery is suppressed (see
    /// <see cref="IMutationObserverHost.MutationDeliverySuppressed"/>). Use with <c>using</c>.
    /// </summary>
    internal MutationDeliverySuppressionScope SuppressMutationDelivery() => new(this);

    /// <summary>Depth-counted RAII scope toggling <see cref="_mutationDeliverySuppressionDepth"/>.</summary>
    internal readonly struct MutationDeliverySuppressionScope : IDisposable
    {
        private readonly DomBridge _bridge;

        internal MutationDeliverySuppressionScope(DomBridge bridge)
        {
            _bridge = bridge;
            _bridge._mutationDeliverySuppressionDepth++;
        }

        public void Dispose() => _bridge._mutationDeliverySuppressionDepth--;
    }
}

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IEventDispatchHost"/>, the narrow contract
/// the extracted <see cref="Broiler.HtmlBridge.Dom.Features.EventDispatchBinding"/> feature module
/// consumes. Explicit interface members, so
/// these seams do not widen the public <c>DomBridge</c> surface.
/// </summary>
/// <remarks>
/// The module speaks JSEAL, and so does every member here. The wrapper cache answers handles,
/// and the document and window wrappers are the bridge's roots, which are the handles the realm
/// minted, so all three forward without converting and <c>event.target === el</c> is the same
/// question it always was. <c>InlineEventHandler</c> below reads a map of handles too.
/// </remarks>
public sealed partial class DomBridge : IEventDispatchHost
{
    JsValue IEventDispatchHost.DocumentWrapper => DocumentHandle;

    JsValue IEventDispatchHost.WindowWrapper => WindowHandle;

    Dictionary<string, List<EventListenerRegistration>> IEventDispatchHost.GetEventListeners(DomNode node) =>
        GetEventListeners(node);

    // The inline on* store holds handles, so the callability test is a read of the stored handle's kind
    // and the module is handed that handle, not a second one minted over the same object. Anything
    // that is not callable answers Missing — and nothing stored can fail the test, because both
    // writers (CompileInlineEventAttribute, and the reflector's setter through its one caller) store
    // only a handle that has answered IsFunction.
    JsValue IEventDispatchHost.InlineEventHandler(DomNode node, string eventType) =>
        GetInlineEventHandlers(node).TryGetValue(eventType, out var handler) && handler.IsFunction
            ? handler
            : JsValue.Missing;
}

// Explicit IEventTargetHost implementation for the EventTargetBinding feature module: the bridge
// exposes the realm, the per-node listener store, the propagation engine and the window JS object via
// explicit interface members, so the module reaches no arbitrary bridge private field and the public
// surface is unchanged. EventTargetBinding calls Features/EventListenerBinding.cs with its call
// frame's realm; the store GetEventListeners hands out holds EventListenerRegistration, whose
// listener field is a handle.
public sealed partial class DomBridge : Dom.Features.IEventTargetHost
{
    Dictionary<string, List<EventListenerRegistration>> Dom.Features.IEventTargetHost.GetEventListeners(DomNode element)
        => GetEventListeners(element);

    // Answers the "not cancelled" boolean the DOM says dispatchEvent returns.
    JsValue Dom.Features.IEventTargetHost.DispatchEvent(DomNode element, JsValue evt)
        => JsValue.Boolean(_eventDispatch.DispatchEventOnElement(element, evt).AsBoolean);

    JsValue Dom.Features.IEventTargetHost.WindowWrapper => WindowHandle;

    bool Dom.Features.IEventTargetHost.TryGetFormControlChecked(DomElement element, out bool value)
        => _formState.TryGetDirtyChecked(element, out value);

    void Dom.Features.IEventTargetHost.SetFormControlChecked(DomElement element, bool value)
        => _formState.SetDirtyChecked(element, value);
}

// Explicit IEventHandlerReflectorHost implementation for the EventHandlerReflectorBinding feature module:
// the bridge exposes the three things the reflector does to the live inline on* handler map
// via explicit interface members, so the reflector module never reaches an arbitrary bridge private
// field and the public surface is unchanged.
//
// The map holds JSEAL handles, and nothing here converts. Every file that touches it is in this
// assembly: the declaration in DomBridge/RuntimeStates.cs, the accessor in DomBridge.cs, the dispatch
// read in the IEventDispatchHost implementation, DomBridge/Events.cs and this implementation.
public sealed partial class DomBridge : Dom.Features.IEventHandlerReflectorHost
{
    JsValue Dom.Features.IEventHandlerReflectorHost.GetInlineEventHandler(DomNode node, string eventName) =>
        // Only functions are ever put in this map. It has two writers: CompileInlineEventAttribute in
        // DomBridge/Events.cs, which stores a compiled handler only when it answered IsFunction, and the
        // setter below, whose one caller, EventHandlerReflectorBinding.SetOn, stores only a handle that
        // answered IsFunction and clears the entry otherwise. CompileInlineEventAttribute is reached
        // by three routes: the CompileInlineEventAttributes loop that runs when an element is first
        // wrapped, and the two attribute-write paths in Features/AttributesBinding.cs. So the object
        // test is what turns nothing stored into the IDL attribute's null, and the stored handle is
        // returned as it is.
        GetInlineEventHandlers(node).TryGetValue(eventName, out var handler) && handler.IsObject
            ? handler
            : JsValue.Null;

    void Dom.Features.IEventHandlerReflectorHost.SetInlineEventHandler(DomNode node, string eventName, JsValue handler) =>
        GetInlineEventHandlers(node)[eventName] = handler;

    void Dom.Features.IEventHandlerReflectorHost.RemoveInlineEventHandler(DomNode node, string eventName) =>
        GetInlineEventHandlers(node).Remove(eventName);
}
