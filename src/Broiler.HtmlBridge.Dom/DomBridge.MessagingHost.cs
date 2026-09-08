using System;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Jseal;

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
/// <remarks>
/// This file is the engine-typed half of the seam and it no longer names an engine type to be it: the
/// contract speaks in <see cref="JsValue"/> handles, and a handle over an object carries the engine's
/// own object, so <see cref="JsInterop"/> carries one across in either direction without converting
/// it. A window that does not exist yet crosses as JavaScript <c>null</c> rather than a CLR one — the
/// module asks <c>IsObject</c> where it used to ask for null, which is the same question and one the
/// compiler cannot silently drop.
/// </remarks>
public sealed partial class DomBridge : IMessagingHost
{
    IJsRealm IMessagingHost.Realm => Realm;

    JsValue IMessagingHost.WindowObject =>
        _windowJSObject is { } window ? JsInterop.FromEngineObject(window) : JsValue.Null;

    string IMessagingHost.PageOrigin => _pageOrigin;

    JsValue IMessagingHost.ResolveCurrentWindow() =>
        ResolveCurrentWindow() is { } window ? JsInterop.FromEngineObject(window) : JsValue.Null;

    JsValue IMessagingHost.ResolveOwnerWindow(JsValue target) =>
        ResolveOwnerWindow(JsInterop.ToEngineObject(target)) is { } window
            ? JsInterop.FromEngineObject(window)
            : JsValue.Null;

    void IMessagingHost.RunWithWindowContext(JsValue targetWindow, Action callback) =>
        RunWithWindowContext(JsInterop.ToEngineObject(targetWindow), callback);

    void IMessagingHost.QueueFrameAction(Action callback) => QueueFrameAction(callback);

    void IMessagingHost.DispatchWindowEvent(JsValue evt) =>
        DispatchWindowEvent(JsInterop.ToEngineObject(evt));
}
