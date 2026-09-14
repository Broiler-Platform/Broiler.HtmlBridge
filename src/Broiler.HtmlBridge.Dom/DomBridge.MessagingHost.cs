using System;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IMessagingHost"/>, the narrow contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.MessagingBinding"/> feature module consumes
/// (HtmlBridge complexity-reduction roadmap Phase 3, P3.10). Each member is an explicit interface
/// implementation, so these seams do not widen the public <c>DomBridge</c> surface. They forward to
/// the browsing-context machinery: top-window dispatch, frame-action queueing, and the window
/// resolution and window-context switch that <c>DomBridge.WindowContext.cs</c> hands to
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
