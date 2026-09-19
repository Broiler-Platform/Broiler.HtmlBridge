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
// Realm is implemented explicitly because DomBridge.Realm is internal, and an
// implicit implementation of a public interface member cannot be satisfied by a non-public property
// (CS0737).
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

    IJsRealm Dom.Features.ILocationHost.Realm => Realm;

    void Dom.Features.ILocationHost.DispatchWindowEvent(JsValue evt)
        => DispatchWindowEvent(evt);

    void Dom.Features.ILocationHost.RequestNavigation(NavigationRequest request)
        => RequestNavigation(request);

    /// <summary>
    /// Records where the page asked to go. Nothing is loaded here — see
    /// <see cref="NavigationRequest"/> for why the decision belongs to the host.
    /// </summary>
    /// <remarks>
    /// Last request wins. A browser starts navigating on the first assignment and supersedes it on
    /// the second, landing on the last one the script asked for before it stopped running; keeping
    /// the first instead would follow a target the page had already changed its mind about.
    /// </remarks>
    private void RequestNavigation(NavigationRequest request)
    {
        if (_pendingNavigation is { } superseded)
        {
            RenderLogger.LogDebug(LogCategory.JavaScript, "DomBridge.location",
                $"{superseded.Url} superseded by {request.Url} before the document settled; the later request is the one that stands");
        }

        _pendingNavigation = request;
    }
}

// Explicit IMatchMediaHost implementation for the MatchMediaBinding feature module (Phase 3):
// the bridge exposes only the live viewport dimensions, via explicit interface members so the
// module never reaches an arbitrary bridge private field and the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IMatchMediaHost
{
    int Dom.Features.IMatchMediaHost.ViewportWidth => _viewportWidth;

    int Dom.Features.IMatchMediaHost.ViewportHeight => _viewportHeight;
}

// Explicit IWindowDocumentMiscHost implementation for the WindowDocumentMiscBinding feature module
// (Phase 3): the bridge exposes the current page URL and the visual-viewport scale setter via explicit
// interface members, so the module never reaches an arbitrary bridge private field and the public
// surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IWindowDocumentMiscHost
{
    string Dom.Features.IWindowDocumentMiscHost.PageUrl => _pageUrl;

    void Dom.Features.IWindowDocumentMiscHost.SetVisualViewportScale(double scale)
        => SetVisualViewportScale(scale);
}

// Explicit IWindowEventTargetHost implementation for the WindowEventTargetBinding feature module
// (Phase 3): the bridge exposes the window's per-type listener store (from the P2.5 EventTargetRegistry)
// and the window-scoped dispatch via explicit interface members, so the module never reaches an
// arbitrary bridge private field and the public surface is unchanged.
//
// This file used to carry ToEngineListenerValue, the one place a JSEAL handle became the engine value
// the listener record held: an unwrap for an object, and a freshly minted engine primitive for
// anything else. The element, document and window contracts' registration members all called it. The
// record holds a handle now, so the converter went with those members rather than moving, and
// WindowEventTargetBinding calls Features/EventListenerBinding.cs itself with its call frame's realm.
public sealed partial class DomBridge : Dom.Features.IWindowEventTargetHost
{
    List<EventListenerRegistration> Dom.Features.IWindowEventTargetHost.WindowListenersForAdd(string type)
        => _eventTargets.WindowListenersForAdd(type);

    bool Dom.Features.IWindowEventTargetHost.TryGetWindowListeners(string type, out List<EventListenerRegistration> listeners)
        => _eventTargets.TryGetWindowListeners(type, out listeners);

    JsValue Dom.Features.IWindowEventTargetHost.DispatchWindowEvent(JsValue evt)
        => JsValue.Boolean(DispatchWindowEvent(evt));
}

// Explicit IWindowScrollHost implementation for the WindowScrollBinding feature module (Phase 3):
// the bridge exposes the document (scrolling) element, the JS scroll-argument parser and the scroll
// primitive via explicit interface members, so the module never reaches an arbitrary bridge private
// field and the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IWindowScrollHost
{
    DomElement Dom.Features.IWindowScrollHost.DocumentElement => DocumentElement;

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
// feature module (Phase 3): the bridge exposes the visual-viewport scroll listener store (from the
// P2.5 EventTargetRegistry) via explicit interface members, so the module never reaches an arbitrary
// bridge private field and the public surface is unchanged.
//
// The contract is spelled in JSEAL handles, and so is the store behind it: Runtime/EventTargetRegistry.cs
// keeps the handle a listener arrived as, and the scroll dispatch in LayoutMetrics.Scrolling.cs invokes
// that handle through the realm, so nothing in the bridge between the page's addEventListener argument
// and that call names an engine type.
//
// This file used to unwrap every listener to the engine's function type, and its comment gave the two
// files either side of it as the reason: the registry "stores the listeners as engine functions", and
// LayoutMetrics.Scrolling.cs "invokes them directly". The first was so only because this file fed the
// registry engine functions. The second was true when it was written and stopped being true a little
// over two hours later, when the dispatcher began converting every listener back into a handle to
// invoke it through the realm — from then on the unwrap here bought that round trip and nothing else.
// The comment also called this "the one place that still has to name the engine's function type",
// while the registry it pointed at spelled that type four times.
//
// The IsFunction test is the half of the old conversion that was a decision rather than a change of
// type, and it stays. VisualViewportEventTargetBinding.IsScrollListener makes the same test first and
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
/// <see cref="WindowContextManager"/> owner consumes (HtmlBridge complexity-reduction roadmap Phase 3,
/// P3.18). Explicit interface members, so these realm seams do not widen the public
/// <c>DomBridge</c> surface — and <see cref="DomBridge.Realm"/> is internal, so an implicit
/// implementation of <see cref="IWindowContextHost.Realm"/> would not compile.
/// </summary>
/// <remarks>
/// Nothing here converts. The window and document are the bridge's roots, which are the handles the
/// realm minted, and the sub-document builder answers a handle of its own, so the window the manager
/// compares against is the same instance by construction rather than by a cast. (This used to call
/// the file the seam's engine-typed half and say all three were wrapped as handles here; the
/// sub-document builder was already forwarded as it stands.)
/// </remarks>
public sealed partial class DomBridge : IWindowContextHost
{
    IJsRealm? IWindowContextHost.Realm => _realm;

    JsValue IWindowContextHost.WindowObject => WindowHandle;

    // Undefined rather than the Missing the root holds, as the member's name says: its one consumer,
    // WindowContextManager.GetWindowDocument, answers undefined on its other branch too.
    JsValue IWindowContextHost.MainDocumentOrUndefined =>
        DocumentHandle.IsMissing ? JsValue.Undefined : DocumentHandle;

    JsValue IWindowContextHost.GetOrCreateSubDocument(DomElement container) =>
        GetOrCreateSubDocument(container);
}

/// <summary>
/// Thin bridge delegators for the browsing-context window-resolution behaviour, which now lives in the
/// single <see cref="Broiler.HtmlBridge.Dom.Runtime.WindowContextManager"/> owner (HtmlBridge
/// complexity-reduction roadmap Phase 3, P3.18 — the last Frames residue; the owner reads the sub-window
/// state from the P3.16 <c>BrowsingContextManager</c>). These forwarders keep the callers unchanged: the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.MessagingBinding"/> reaches them through the
/// <see cref="Broiler.HtmlBridge.Dom.Features.IMessagingHost"/> contract, and the sub-document script
/// runner calls <c>RunWithWindowContext</c> directly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every delegator here speaks JSEAL now, and the last one that did not was held open by a
/// comment naming a caller it did not have.</b> Several of these used to take or answer the engine's
/// own object while both the manager below them and the callers above them held handles, so a window
/// was converted out and converted straight back for no reader. The last of them was a second
/// <c>RunWithWindowContext</c> overload taking the engine's object, and the remarks that kept it said
/// its caller was the sub-document script runner in <c>DomBridge/SubDocuments.cs</c>. It was not:
/// that call site is one unchanged line, and it has bound to the overload below ever since
/// <c>SubWindowBinding.GetOrCreate</c> was re-typed to answer a handle — which
/// <c>FrameScriptExecutionTests</c> states in its own header, having been written to cover exactly
/// that rebinding.
/// </para>
/// <para>
/// The only caller the deleted overload had was the <see cref="IMessagingHost"/> implementation below, which unwrapped a
/// handle to an engine object so that the overload could wrap the same object straight back before
/// handing it to the manager, whose own <c>RunWithWindowContext</c> has taken a handle all along.
/// All three callers of <see cref="RunWithWindowContext(JsValue, Action)"/> — that host,
/// <c>DomBridge/SubDocuments.Loading.cs</c> and <c>DomBridge/SubDocuments.cs</c> — already hold
/// one, so there is nothing left for an engine-typed spelling to save any of them.
/// </para>
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
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.MessagingBinding"/> feature module consumes
/// (HtmlBridge complexity-reduction roadmap Phase 3, P3.10). Each member is an explicit interface
/// implementation, so these seams do not widen the public <c>DomBridge</c> surface. They forward to
/// the browsing-context machinery: top-window dispatch, frame-action queueing, and the window
/// resolution and window-context switch that the delegators above hand to
/// <c>WindowContextManager</c>. (This said "pending a future <c>BrowsingContextManager</c>".)
/// </summary>
/// <remarks>
/// Nothing here converts. The contract speaks in <see cref="JsValue"/> handles and the window is the
/// bridge's root, which is one. (This used to describe the file as the half of the seam where a cast
/// carried the window across.) A window that does not exist yet crosses as JavaScript <c>null</c>, not
/// as the <see cref="JsValue.Missing"/> the root holds, and that is kept rather than collapsed: the
/// module asks <c>IsObject</c> in most places, which reads the two alike, but it also compares a
/// target window against this member with handle equality, and there null and Missing are different
/// answers.
/// </remarks>
public sealed partial class DomBridge : IMessagingHost
{
    IJsRealm IMessagingHost.Realm => Realm;

    JsValue IMessagingHost.WindowObject =>
        WindowHandle.IsMissing ? JsValue.Null : WindowHandle;

    string IMessagingHost.PageOrigin => _pageOrigin;

    // Both answer JsValue.Null for "no window" themselves now, so this is the delegation it reads
    // as rather than a conversion around one.
    JsValue IMessagingHost.ResolveCurrentWindow() => ResolveCurrentWindow();

    JsValue IMessagingHost.ResolveOwnerWindow(JsValue target) => ResolveOwnerWindow(target);

    // The unwrap this used to do bought one thing: it picked the engine-typed overload, which
    // wrapped the same object straight back. That overload is gone and the handle travels as it
    // stands, which is what the manager below has always taken.
    void IMessagingHost.RunWithWindowContext(JsValue targetWindow, Action callback) =>
        RunWithWindowContext(targetWindow, callback);

    void IMessagingHost.QueueFrameAction(Action callback) => QueueFrameAction(callback);

    void IMessagingHost.DispatchWindowEvent(JsValue evt) => DispatchWindowEvent(evt);
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
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.FetchBinding"/> feature module consumes
/// (HtmlBridge complexity-reduction roadmap Phase 3, P3.11). Every member is an explicit interface
/// implementation, so none of them widens the public <c>DomBridge</c> surface.
/// </summary>
/// <remarks>
/// <b>There was never an engine reference here, and the sentence that used to stand in this place
/// counted wrong.</b> It said one was left and that the wrapper registry pinned it. What was left was
/// a <c>JsInterop</c> crossing, and a crossing names no engine type: <c>eng/jseal-budget.json</c>
/// counts the engine's namespace as text, and this file has never contributed a single occurrence of
/// it. The crossing is gone too — the wrapper-to-node lookup takes the handle now, and the registry
/// behind it is keyed on <see cref="JsValue.ObjectIdentity"/> rather than on an engine object — and
/// nothing in <c>FetchBinding</c> changed when it went.
/// </remarks>
public sealed partial class DomBridge : IFetchHost
{
    /// <inheritdoc />
    IJsRealm IFetchHost.Realm => Realm;

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

    string IFetchHost.PageUrl => _pageUrl;

    JsValue IFetchHost.StreamOverText(string text) => _streams.StreamOverText(text);

    JsValue IFetchHost.StreamOverTextObserved(string text, System.Action onDisturbed) =>
        _streams.StreamOverTextObserved(text, onDisturbed);

    bool IFetchHost.IsStreamLocked(JsValue stream) => _streams.IsStreamLocked(stream);

    JsValue IFetchHost.CreateBlob(byte[] bytes, string contentType) =>
        _blobs.CreateBlobFromBytes(Realm, bytes, contentType);
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
// It used to go through the eval-gated member, and the argument for that was provenance: the text is
// the page's and not this repository's, which is true and is not what decides the member. What
// decides it is which Content-Security-Policy directive governs the source. A script element is
// script-src's -- per script, satisfied by 'unsafe-inline', a nonce or a hash -- and the decision has
// already been taken, by ScriptInsertionRunner, before this is called. 'unsafe-eval' governs eval and
// new Function and has nothing to say about this text, so routing it through the eval-gated member
// would have refused, on a realm narrowed by a restrictive policy, a script every browser runs.
//
// The dispatch call is not engine-typed either: FireSimpleEvent builds its event through the realm,
// and Features/EventDispatchBinding.cs's DispatchEventOnElement takes that handle as it is.
public sealed partial class DomBridge : Dom.Runtime.IScriptInsertionHost
{
    DomDocument Dom.Runtime.IScriptInsertionHost.Document => _document;

    bool Dom.Runtime.IScriptInsertionHost.HasRealm => _realm is not null;

    string Dom.Runtime.IScriptInsertionHost.PageUrl => _pageUrl;

    ContentSecurityPolicy? Dom.Runtime.IScriptInsertionHost.Csp => Csp;

    bool Dom.Runtime.IScriptInsertionHost.MutationDeliverySuppressed => _mutationDeliverySuppressionDepth > 0;

    void Dom.Runtime.IScriptInsertionHost.QueueTask(Action task) => _eventLoop.QueueTask(task);

    void Dom.Runtime.IScriptInsertionHost.EvaluateScript(string source, string label)
    {
        // A script body is a turn too, and the one most likely to be the long pole at load; see
        // JsEntryTrace. Inactive by default.
        using var turn = JsEntryTrace.Enter(JsEntryKind.Script, label);
        _realm?.EvaluateClassicScript(source, label);
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
/// feature module consumes (HtmlBridge complexity-reduction roadmap Phase 3). Explicit interface
/// members, so these seams do not widen the public <c>DomBridge</c> surface.
/// </summary>
/// <remarks>
/// <b>The wrapper cache the bridge keys on is not an engine one, and this said it was.</b>
/// <c>Runtime/JsObjectRegistry</c> was re-typed onto <see cref="JsValue"/> and keys on
/// <see cref="JsValue.ObjectIdentity"/>; with the reverse lookup taking a handle as well there is no
/// cast left in this file. A record's <c>target</c> is the same wrapper instance a page already
/// holds because it is the same handle, not because one was unwrapped and re-wrapped around it.
/// </remarks>
public sealed partial class DomBridge : IMutationObserverHost
{
    IJsRealm IMutationObserverHost.Realm => Realm;

    JsValue IMutationObserverHost.WrapNode(DomNode node) => WrapNode(node);

    DomNode? IMutationObserverHost.FindNode(JsValue wrapper) =>
        wrapper.IsObject ? FindDomNodeByJSObject(wrapper) : null;

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
/// consumes (HtmlBridge complexity-reduction roadmap Phase 3, P3.3). Explicit interface members, so
/// these seams do not widen the public <c>DomBridge</c> surface.
/// </summary>
/// <remarks>
/// The module speaks JSEAL, and so does every member here. The wrapper cache answers handles,
/// and the document and window wrappers are the bridge's roots, which are the handles the realm
/// minted, so all three forward without converting and <c>event.target === el</c> is the same
/// question it always was. (This used to call all three engine objects, with a cast between them.)
/// <c>InlineEventHandler</c> below reads a map of handles too; this called it the one crossing left.
/// </remarks>
public sealed partial class DomBridge : IEventDispatchHost
{
    IJsRealm IEventDispatchHost.Realm => Realm;

    JsValue IEventDispatchHost.WrapNode(DomNode node) => WrapNode(node);

    DomNode IEventDispatchHost.DocumentNode => _document;

    JsValue IEventDispatchHost.DocumentWrapper => DocumentHandle;

    JsValue IEventDispatchHost.WindowWrapper => WindowHandle;

    Dictionary<string, List<EventListenerRegistration>> IEventDispatchHost.GetEventListeners(DomNode node) =>
        GetEventListeners(node);

    // The inline on* store holds handles, so the callability test is a read of the stored handle's kind
    // and the module is handed that handle, not a second one minted over the same object. This comment
    // used to say the test had to stay on the engine side because the store was a dictionary of engine
    // values in a file this round did not own. The store's only other readers and writers were
    // DomBridge/Events.cs and the IEventHandlerReflectorHost implementation below, and all three moved
    // together. Anything that is not callable still answers Missing, as the failed type pattern did —
    // and nothing stored can fail it, because both writers (CompileInlineEventAttribute, and the
    // reflector's setter through its one caller) store only a handle that has answered IsFunction.
    JsValue IEventDispatchHost.InlineEventHandler(DomNode node, string eventType) =>
        GetInlineEventHandlers(node).TryGetValue(eventType, out var handler) && handler.IsFunction
            ? handler
            : JsValue.Missing;
}

// Explicit IEventTargetHost implementation for the EventTargetBinding feature module (Phase 3): the
// bridge exposes the realm, the per-node listener store, the propagation engine and the window JS
// object via explicit interface members, so the module reaches no arbitrary bridge private field and
// the public surface is unchanged.
//
// A registration pair used to sit here too, and it was the seam the document and window contracts
// shared: a registration's listener field was an engine value, so a handle became one here, through
// ToEngineListenerValue in the IWindowEventTargetHost implementation. The record holds a handle now, so the
// pair and the converter are deleted and EventTargetBinding calls Features/EventListenerBinding.cs
// with its call frame's realm. The store GetEventListeners hands out has that record as its element
// type, and the record no longer holds an engine value.
//
// An engine-typed DispatchEventOnElement sat beside the migrated one until this file said it was
// what "the pre-realm wrapper path in DomBridge/JsObjects.cs still needs". That path does not name
// this contract at all, and the member had no caller anywhere; it is deleted rather than ported.
public sealed partial class DomBridge : Dom.Features.IEventTargetHost
{
    IJsRealm Dom.Features.IEventTargetHost.Realm => Realm;

    Dictionary<string, List<EventListenerRegistration>> Dom.Features.IEventTargetHost.GetEventListeners(DomNode element)
        => GetEventListeners(element);

    // Answers the "not cancelled" boolean the DOM says dispatchEvent returns.
    JsValue Dom.Features.IEventTargetHost.DispatchEvent(DomNode element, JsValue evt)
        => JsValue.Boolean(_eventDispatch.DispatchEventOnElement(element, evt).AsBoolean);

    JsValue Dom.Features.IEventTargetHost.WindowWrapper => WindowHandle;

    FormControlRuntimeState Dom.Features.IEventTargetHost.FormControlStateFor(DomElement element)
        => FormControlStateFor(element);

    void Dom.Features.IEventTargetHost.UncheckRadioSiblings(DomElement scope, DomElement except, string radioName)
        => UncheckRadioSiblings(scope, except, radioName);
}

// Explicit IEventHandlerReflectorHost implementation for the EventHandlerReflectorBinding feature module
// (Phase 3): the bridge exposes the three things the reflector does to the live inline on* handler map
// via explicit interface members, so the reflector module never reaches an arbitrary bridge private
// field and the public surface is unchanged.
//
// The map holds JSEAL handles, and nothing here converts. This paragraph used to say the map held the
// engine's own value type because three unowned files decided it — the declaration in
// DomBridge/RuntimeStates.cs, the accessor in DomBridge.cs and the dispatch read in
// the IEventDispatchHost implementation — and that the two casts in this file would go "when the record
// moves". Those three, with DomBridge/Events.cs and this implementation, are every file that touches the map, so
// nothing outside the change was deciding anything. It also called DomBridge/Events.cs the only writer
// that went through the realm, when the setter below stored a realm-minted argument too. And the
// record it meant, EventListenerRegistration, is a different declaration that shares
// DomBridge/RuntimeStates.cs with the map; the casts did not wait for it.
public sealed partial class DomBridge : Dom.Features.IEventHandlerReflectorHost
{
    JsValue Dom.Features.IEventHandlerReflectorHost.GetInlineEventHandler(DomNode node, string eventName) =>
        // Only functions are ever put in this map. It has two writers: CompileInlineEventAttribute in
        // DomBridge/Events.cs, which stores a compiled handler only when it answered IsFunction, and the
        // setter below, whose one caller, EventHandlerReflectorBinding.SetOn, stores only a handle that
        // answered IsFunction and clears the entry otherwise. (This used to name the first writer as
        // CompileInlineEventAttributes, the loop that runs when an element is first wrapped. That is one
        // of three routes into it; the other two are the attribute-write paths in
        // Features/AttributesBinding.cs.) So the object test is what turns nothing stored into the IDL
        // attribute's null. The stored handle is returned as it is. The handle this used to mint over
        // the same object compared equal to it: the provider and the bridge's seam both give an engine
        // function kind Function, and a handle compares kind and then reference.
        GetInlineEventHandlers(node).TryGetValue(eventName, out var handler) && handler.IsObject
            ? handler
            : JsValue.Null;

    void Dom.Features.IEventHandlerReflectorHost.SetInlineEventHandler(DomNode node, string eventName, JsValue handler) =>
        GetInlineEventHandlers(node)[eventName] = handler;

    void Dom.Features.IEventHandlerReflectorHost.RemoveInlineEventHandler(DomNode node, string eventName) =>
        GetInlineEventHandlers(node).Remove(eventName);
}
