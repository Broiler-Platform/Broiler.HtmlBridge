using System;
using System.Collections.Generic;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow bridge services the <see cref="MessagingBinding"/> feature module needs (HtmlBridge
/// complexity-reduction roadmap Phase 3, P3.10). Web messaging (<c>window.postMessage</c>,
/// <c>MessageChannel</c>/<c>MessagePort</c>) and the generic <c>EventTarget</c> dispatch it shares
/// with sub-windows are deeply entangled with the document's browsing-context state — the active
/// window override, the sub-window/sub-document caches and the window-context switch — which the
/// Phase 2 work deliberately left in the bridge (a future <c>BrowsingContextManager</c>). Rather than
/// drag that state into the module, the module reaches the few browsing-context operations it needs
/// through these named seams, exposed as explicit interface members on <see cref="DomBridge"/> so the
/// public surface is unchanged.
/// </summary>
/// <remarks>
/// The vocabulary is JSEAL's: a window, a port and an event are <see cref="JsValue"/> handles, and
/// the realm replaces the former <c>JsContext</c> seam. That seam existed for exactly two purposes —
/// raising a <c>DataCloneError</c>, which <see cref="IJsCalls.DomError"/> now owns, and structured-
/// cloning a message payload, which nothing in JSEAL covers and which is therefore the one thing
/// <see cref="MessagingBinding"/> still does in the engine's own vocabulary.
/// </remarks>
internal interface IMessagingHost
{
    /// <summary>
    /// The realm the messaging objects are built in, and through which this module raises a
    /// <c>DOMException</c>. Never null while a document is attached; the messaging APIs are only
    /// reachable from an attached document.
    /// </summary>
    IJsRealm Realm { get; }

    /// <inheritdoc cref="EventListenerBinding.AddListener" />
    /// <remarks>
    /// The same seam <see cref="IEventTargetHost.AddListener"/> is, and for the same reason: a
    /// listener record holds the engine's value, so the conversion belongs in the host rather than
    /// in a module that would otherwise need an argument frame to reach one.
    /// </remarks>
    void AddListener(List<EventListenerRegistration> listeners, JsValue listener, JsValue options);

    /// <inheritdoc cref="EventListenerBinding.RemoveListener" />
    void RemoveListener(List<EventListenerRegistration>? listeners, JsValue listener, JsValue options);

    /// <summary>The top-level window wrapper (<see cref="JsValue.Null"/> before attach).</summary>
    JsValue WindowObject { get; }

    /// <summary>
    /// The document's own origin, as parsed from the page URL at attach.
    /// <para>
    /// The top window's origin cannot be read back out of its <c>location</c> property: the window
    /// IS the global object, and <see cref="RunWithWindowContext"/> temporarily swaps
    /// <c>location</c> (with <c>document</c>, <c>self</c>, <c>parent</c> and the rest) to a frame's
    /// while that frame's scripts run — which is exactly when a frame posts to its parent. Reading
    /// the property there yields the frame's own <c>about:srcdoc</c>, not the page's origin. This is
    /// the fact rather than the mutable view of it.
    /// </para>
    /// </summary>
    string PageOrigin { get; }

    /// <summary>
    /// Resolves the window currently driving script execution (honouring the active sub-window
    /// override), canonicalised to the owning browsing context. Not an object when there is none.
    /// </summary>
    JsValue ResolveCurrentWindow();

    /// <summary>
    /// Resolves the window that owns <paramref name="target"/> (a message port or generic event
    /// target), falling back to the current window. Not an object when there is none.
    /// </summary>
    JsValue ResolveOwnerWindow(JsValue target);

    /// <summary>Runs <paramref name="callback"/> with the global window/document/location/parent
    /// bindings temporarily switched to <paramref name="targetWindow"/>'s browsing context.</summary>
    void RunWithWindowContext(JsValue targetWindow, Action callback);

    /// <summary>Queues <paramref name="callback"/> as an internal frame action on the event loop
    /// (message delivery is asynchronous, per the HTML messaging model).</summary>
    void QueueFrameAction(Action callback);

    /// <summary>Dispatches <paramref name="evt"/> at the top-level window (the fast path when a
    /// posted message targets the main window itself).</summary>
    void DispatchWindowEvent(JsValue evt);
}
