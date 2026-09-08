using System;
using Broiler.HtmlBridge.Jseal;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

/// <summary>
/// Thin bridge delegators for the browsing-context window-resolution behaviour, which now lives in the
/// single <see cref="Broiler.HtmlBridge.Dom.Runtime.WindowContextManager"/> owner (HtmlBridge
/// complexity-reduction roadmap Phase 3, P3.18 — the last Frames residue; the owner reads the sub-window
/// state from the P3.16 <c>BrowsingContextManager</c>). These forwarders keep the callers unchanged: the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.MessagingBinding"/> reaches them through the
/// <see cref="Broiler.HtmlBridge.Dom.Features.IMessagingHost"/> contract, and the sub-document script
/// runner calls the engine-typed <c>RunWithWindowContext</c> overload directly.
/// </summary>
/// <remarks>
/// <para>
/// <b>The owner speaks JSEAL; three of these delegators do not, and that is what this file is for.</b>
/// <c>DomBridge.MessagingHost.cs</c> and <c>DomBridge/SubDocuments.cs</c> are other groups' files this
/// round and hold a window as the engine's own object — the messaging host even unwraps a handle to
/// call in — so the engine-typed forms below stay exactly as they were and convert at the boundary.
/// A JSEAL handle carries the engine object, so each conversion is a cast and the window identity is
/// unchanged either way.
/// </para>
/// <para>
/// The <see cref="RunWithWindowContext(JsValue, Action)"/> overload is the same call for a caller that
/// already holds a handle (<c>DomBridge.SubDocumentGlobals.cs</c>), and exists so that caller does not
/// unwrap only for this file to wrap again.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>The engine object behind a window handle, or <see langword="null"/> when the manager
    /// answered "no window" — the CLR null these engine-typed forms have always returned.</summary>
    private static JSObject? ToEngineWindow(JsValue window) =>
        window.IsObject ? Dom.Runtime.JsInterop.ToEngineObject(window) : null;

    private JSObject? ResolveCurrentWindow() => ToEngineWindow(_windowContext.ResolveCurrentWindow());

    private JSObject? ResolveOwnerWindow(JSObject target) =>
        ToEngineWindow(_windowContext.ResolveOwnerWindow(Dom.Runtime.JsInterop.FromEngineObject(target)));

    private JsValue GetCanonicalWindow(JsValue candidate) => _windowContext.GetCanonicalWindow(candidate);

    private void RunWithWindowContext(JSObject targetWindow, Action callback) =>
        _windowContext.RunWithWindowContext(Dom.Runtime.JsInterop.FromEngineObject(targetWindow), callback);

    private void RunWithWindowContext(JsValue targetWindow, Action callback) =>
        _windowContext.RunWithWindowContext(targetWindow, callback);

    private JsValue GetWindowDocument(JsValue targetWindow) => _windowContext.GetWindowDocument(targetWindow);

    private JsValue GetWindowParent(JsValue targetWindow) => _windowContext.GetWindowParent(targetWindow);
}
