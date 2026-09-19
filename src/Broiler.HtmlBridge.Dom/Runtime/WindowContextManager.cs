using System;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// The single owner of a document's browsing-context window-resolution behaviour (HtmlBridge
/// complexity-reduction roadmap Phase 3, P3.18 — the last Frames residue): canonicalising a window
/// candidate against the sub-window state, resolving the current/owner window, and temporarily switching
/// the global <c>window</c>/<c>document</c>/<c>location</c>/<c>parent</c>/<c>postMessage</c>/<c>self</c>/
/// <c>top</c> bindings into another browsing context while a callback runs. It reads the sub-window
/// identity from the P3.16 <see cref="BrowsingContextManager"/> and the owner-window map from the shared
/// <see cref="EventTargetRegistry"/> (both held directly), and reaches the realm and the sub-document
/// builder through the narrow <see cref="IWindowContextHost"/> contract.
/// </summary>
/// <remarks>
/// <para>
/// The bridge's window-context methods in <c>DomBridge/Hosts.Window.cs</c> are now thin delegators to this owner (the same
/// P2.4/P2.5/P2.6 "state/behaviour owner, bridge forwards" shape), so the callers — <c>MessagingBinding</c>
/// via <c>IMessagingHost</c> and the sub-document script runner — are unchanged.
/// </para>
/// <para>
/// <b>A window is a <see cref="JsValue"/> here, and the seven globals are saved and restored through the
/// realm.</b> The seven evaluations below are host script by the contract's definition — this repository
/// authored every one of them, they read a global and nothing else, and none is subject to the page's
/// content policy — so they go through <see cref="IJsSource.EvaluateHostScript"/>. Nothing here is left
/// engine-typed. The sub-window <em>identity</em> state is still <see cref="BrowsingContextManager"/>'s
/// alone, and it holds <see cref="JsValue"/> handles now, so the four members of it read below take and
/// answer the handles this file already holds. This paragraph used to say that state was keyed on the
/// engine's own object and that the handle was unwrapped at that boundary and nowhere else. The
/// boundary was real, and it cost five crossings — three unwraps and two re-wraps, in three private
/// adapters and one loop — every one of them there for that state's engine type alone.
/// </para>
/// <para>
/// <b><see cref="EventTargetRegistry"/> was named alongside it here, and had already stopped keying on
/// the engine's object when this file said so.</b> That sentence was true when it was written and was
/// falsified by the commit that re-typed the listener stores onto <see cref="JsValue"/>; what survived
/// it was an engine-typed owner-window accessor whose own doc comment justified itself by pointing back
/// at this file. Both sides are handles now, and the two conversions this file made around that call
/// are gone.
/// </para>
/// <para>
/// <b>An absent window is <see cref="JsValue.Missing"/> where it used to be a CLR <see langword="null"/>.</b>
/// Every place that tested for one now asks <see cref="JsValue.IsObject"/>, which is the same question:
/// the old code's narrowing cast answered null for a primitive as well as for nothing at all.
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
            // there; the bridge's own top-level window otherwise. A non-object answer is no answer,
            // exactly as the former narrowing cast to the engine's object type made it.
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
    /// page's. <c>OwnerWindowRoutingTests</c> is that test. This paragraph used to end by saying nothing
    /// under <c>src/Broiler.Browser.Core.Tests</c> named an owner window at all, and the commit after the
    /// one that wrote it added that file.
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

            // Two non-object parents count as the same parent, which is what the former
            // reference-equality of the two `parent` reads after narrowing each to an object
            // said: anything that is not an object narrowed to null on both sides.
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

    public void RunWithWindowContext(JsValue targetWindow, Action callback)
    {
        if (_host.Realm is not { } realm)
        {
            callback();
            return;
        }

        // Undefined rather than "unset": an evaluation below that throws leaves its slot at this
        // value, and the restore then clears the binding — which is what the former null-coalescing
        // restore did with a slot that was never filled.
        var previousWindow = JsValue.Undefined;
        var previousDocument = JsValue.Undefined;
        var previousLocation = JsValue.Undefined;
        var previousParent = JsValue.Undefined;
        var previousPostMessage = JsValue.Undefined;
        var previousSelf = JsValue.Undefined;
        var previousTop = JsValue.Undefined;
        var previousCurrentWindow = _browsingContexts.CurrentWindowOverride;

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

            callback();
        }
        finally
        {
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
        // that is what `this` evaluates to at the top level — the same object the former Eval("this")
        // produced, asked for directly rather than compiled for.
        if (IsMainWindow(targetWindow))
            return _host.Realm is { } mainRealm ? mainRealm.Global : targetWindow;

        if (_host.Realm is { } realm && realm.GetProperty(targetWindow, "parent") is { IsMissing: false } parent)
            return parent;

        return _host.WindowObject.IsObject ? _host.WindowObject : targetWindow;
    }

    private bool IsMainWindow(JsValue window) => window.IsObject && window == _host.WindowObject;
}
