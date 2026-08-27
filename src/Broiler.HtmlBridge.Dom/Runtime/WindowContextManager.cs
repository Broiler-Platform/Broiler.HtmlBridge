using System;
using Broiler.Dom;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// The single owner of a document's browsing-context window-resolution behaviour (HtmlBridge
/// complexity-reduction roadmap Phase 3, P3.18 — the last Frames residue): canonicalising a window
/// candidate against the sub-window state, resolving the current/owner window, and temporarily switching
/// the global <c>window</c>/<c>document</c>/<c>location</c>/<c>parent</c>/<c>postMessage</c>/<c>self</c>/
/// <c>top</c> bindings into another browsing context while a callback runs. It reads the sub-window
/// identity from the P3.16 <see cref="BrowsingContextManager"/> and the owner-window map from the shared
/// <see cref="EventTargetRegistry"/> (both held directly), and reaches the JS context and the sub-document
/// builder through the narrow <see cref="IWindowContextHost"/> contract.
/// </summary>
/// <remarks>
/// The bridge's <c>DomBridge.WindowContext.cs</c> methods are now thin delegators to this owner (the same
/// P2.4/P2.5/P2.6 "state/behaviour owner, bridge forwards" shape), so the callers — <c>MessagingBinding</c>
/// via <c>IMessagingHost</c> and the sub-document script runner — are unchanged.
/// </remarks>
internal sealed class WindowContextManager(
    IWindowContextHost host,
    BrowsingContextManager browsingContexts,
    EventTargetRegistry eventTargets)
{
    private readonly IWindowContextHost _host = host;
    private readonly BrowsingContextManager _browsingContexts = browsingContexts;
    private readonly EventTargetRegistry _eventTargets = eventTargets;

    public JSObject? ResolveCurrentWindow()
        => GetCanonicalWindow(_browsingContexts.CurrentWindowOverride ?? _host.GetGlobal("window") as JSObject ?? _host.WindowJSObject);

    /// <summary>
    /// The sub-window whose browsing context is current, or <c>null</c> when that is the main window
    /// (or there is no window at all). What a caller registering deferred work needs in order to ask
    /// "was this handed to me by a frame?" — the main-window answer being the one it can ignore,
    /// since the main context is the one everything already runs in.
    /// </summary>
    public JSObject? ResolveCurrentSubWindow()
        => ResolveCurrentWindow() is { } current && _browsingContexts.IsSubWindow(current) ? current : null;

    public JSObject? ResolveOwnerWindow(JSObject target)
        => _eventTargets.TryGetOwnerWindow(target, out var ownerWindow) ? GetCanonicalWindow(ownerWindow) : ResolveCurrentWindow();

    public JSObject? GetCanonicalWindow(JSObject? candidate)
    {
        if (candidate == null || ReferenceEquals(candidate, _host.WindowJSObject))
            return candidate;

        if (_browsingContexts.IsSubWindow(candidate))
            return candidate;

        foreach (var subWindow in _browsingContexts.SubWindows)
        {
            if (ReferenceEquals(candidate, subWindow))
                return subWindow;

            var candidateHref = (candidate[(KeyString)"location"] as JSObject)?[(KeyString)"href"]?.ToString();
            var subWindowHref = (subWindow[(KeyString)"location"] as JSObject)?[(KeyString)"href"]?.ToString();
            if (!string.Equals(candidateHref, subWindowHref, StringComparison.Ordinal))
                continue;

            var candidateParent = candidate[(KeyString)"parent"] as JSObject;
            var subWindowParent = subWindow[(KeyString)"parent"] as JSObject;
            if (ReferenceEquals(candidateParent, subWindowParent))
                return subWindow;
        }

        return candidate;
    }

    public void RunWithWindowContext(JSObject targetWindow, Action callback)
    {
        if (!_host.HasJsContext)
        {
            callback();
            return;
        }

        JSValue? previousWindow = null;
        JSValue? previousDocument = null;
        JSValue? previousLocation = null;
        JSValue? previousParent = null;
        JSValue? previousPostMessage = null;
        JSValue? previousSelf = null;
        JSValue? previousTop = null;
        var previousCurrentWindow = _browsingContexts.CurrentWindowOverride;

        try
        {
            previousWindow = _host.Eval("typeof window === 'undefined' ? undefined : window");
            previousDocument = _host.Eval("typeof document === 'undefined' ? undefined : document");
            previousLocation = _host.Eval("typeof location === 'undefined' ? undefined : location");
            previousParent = _host.Eval("typeof parent === 'undefined' ? undefined : parent");
            previousPostMessage = _host.Eval("typeof postMessage === 'undefined' ? undefined : postMessage");
            previousSelf = _host.Eval("typeof self === 'undefined' ? undefined : self");
            previousTop = _host.Eval("typeof top === 'undefined' ? undefined : top");

            _host.SetGlobal("window", targetWindow);
            _host.SetGlobal("document", GetWindowDocument(targetWindow));
            _host.SetGlobal("location", targetWindow[(KeyString)"location"] ?? JSUndefined.Value);
            _host.SetGlobal("parent", GetWindowParent(targetWindow));
            _host.SetGlobal("postMessage", targetWindow[(KeyString)"postMessage"] ?? JSUndefined.Value);
            _host.SetGlobal("self", targetWindow);
            _host.SetGlobal("top", _host.WindowJSObject ?? targetWindow);
            _browsingContexts.CurrentWindowOverride = targetWindow;

            callback();
        }
        finally
        {
            _host.SetGlobal("window", previousWindow ?? JSUndefined.Value);
            _host.SetGlobal("document", previousDocument ?? JSUndefined.Value);
            _host.SetGlobal("location", previousLocation ?? JSUndefined.Value);
            _host.SetGlobal("parent", previousParent ?? JSUndefined.Value);
            _host.SetGlobal("postMessage", previousPostMessage ?? JSUndefined.Value);
            _host.SetGlobal("self", previousSelf ?? JSUndefined.Value);
            _host.SetGlobal("top", previousTop ?? JSUndefined.Value);
            _browsingContexts.CurrentWindowOverride = previousCurrentWindow;
        }
    }

    public JSValue GetWindowDocument(JSObject targetWindow)
    {
        if (ReferenceEquals(targetWindow, _host.WindowJSObject))
            return _host.MainDocumentOrUndefined;

        return _browsingContexts.TryGetSubWindowContainer(targetWindow, out var containerElement)
            ? _host.GetOrCreateSubDocument(containerElement)
            : JSUndefined.Value;
    }

    public JSValue GetWindowParent(JSObject targetWindow)
    {
        if (ReferenceEquals(targetWindow, _host.WindowJSObject))
            return _host.Eval("this") ?? targetWindow;

        return targetWindow[(KeyString)"parent"] ?? (JSValue?)_host.WindowJSObject ?? targetWindow;
    }
}
