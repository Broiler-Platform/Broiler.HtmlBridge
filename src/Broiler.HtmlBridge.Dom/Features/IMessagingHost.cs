using System;
using System.Collections.Generic;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow bridge services the <see cref="MessagingBinding"/> feature module needs. Web messaging
/// (<c>window.postMessage</c>,
/// <c>MessageChannel</c>/<c>MessagePort</c>) and the generic <c>EventTarget</c> dispatch it shares
/// with sub-windows are deeply entangled with the document's browsing-context state — the active
/// window override, the sub-window/sub-document caches and the window-context switch — which lives in
/// <c>BrowsingContextManager</c> and <c>WindowContextManager</c>. Rather than
/// drag that state into the module, the module reaches the few browsing-context operations it needs
/// through these named seams, exposed as explicit interface members on <see cref="DomBridge"/> so the
/// public surface is unchanged.
/// </summary>
/// <remarks>
/// The vocabulary is JSEAL's: a window, a port and an event are <see cref="JsValue"/> handles, and the
/// realm is the only engine seam. A <c>DataCloneError</c> is raised through
/// <see cref="IJsCalls.DomError"/>, and a message payload is structured-cloned through
/// <see cref="IJsClone.Clone"/>.
/// <para>
/// The realm is never null while a document is attached, and the messaging APIs are only reachable
/// from one — so this contract takes the property rather than a nullable realm of its own.
/// </para>
/// </remarks>
internal interface IMessagingHost : IRealmHost
{
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

    /// <summary>
    /// The origin of the document a frame's <paramref name="window"/> shows, serialized as
    /// <c>MessageEvent.origin</c> is (<c>"null"</c> for an opaque one), taken from that document's
    /// request context -- or <see langword="null"/> when <paramref name="window"/> is not a frame's
    /// window. Never read from anything the frame's script can assign.
    /// </summary>
    string? FrameWindowOrigin(JsValue window);

    /// <summary>
    /// Whether the documents two windows show -- the top window's or frames' -- have different
    /// origins, judged from their request contexts.
    /// </summary>
    bool AreWindowsCrossOrigin(JsValue first, JsValue second);
}
