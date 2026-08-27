using System;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IMessagingHost"/>, the narrow contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.MessagingBinding"/> feature module consumes
/// (HtmlBridge complexity-reduction roadmap Phase 3, P3.10). Each member is an explicit interface
/// implementation, so these seams do not widen the public <c>DomBridge</c> surface. They forward to
/// the browsing-context machinery that Phase 2 deliberately kept in the bridge (window resolution,
/// the window-context switch, top-window dispatch and frame-action queueing — see
/// <c>DomBridge.WindowContext.cs</c>), pending a future <c>BrowsingContextManager</c>.
/// </summary>
public sealed partial class DomBridge : IMessagingHost
{
    JSObject? IMessagingHost.WindowJSObject => _windowJSObject;

    string IMessagingHost.PageOrigin => _pageOrigin;

    JSContext? IMessagingHost.JsContext => _jsContext;

    JSObject? IMessagingHost.ResolveCurrentWindow() => ResolveCurrentWindow();

    JSObject? IMessagingHost.ResolveOwnerWindow(JSObject target) => ResolveOwnerWindow(target);

    void IMessagingHost.RunWithWindowContext(JSObject targetWindow, Action callback) =>
        RunWithWindowContext(targetWindow, callback);

    void IMessagingHost.QueueFrameAction(Action callback) => QueueFrameAction(callback);

    void IMessagingHost.DispatchWindowEvent(JSObject evt) => DispatchWindowEvent(evt);
}
