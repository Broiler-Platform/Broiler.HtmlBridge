using System;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

/// <summary>
/// Thin bridge delegators for the browsing-context window-resolution behaviour, which now lives in the
/// single <see cref="Broiler.HtmlBridge.Dom.Runtime.WindowContextManager"/> owner (HtmlBridge
/// complexity-reduction roadmap Phase 3, P3.18 — the last Frames residue; the owner reads the sub-window
/// state from the P3.16 <c>BrowsingContextManager</c>). These forwarders keep the callers unchanged: the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.MessagingBinding"/> reaches them through the
/// <see cref="Broiler.HtmlBridge.Dom.Features.IMessagingHost"/> contract, and the sub-document script
/// runner calls <see cref="RunWithWindowContext"/> directly.
/// </summary>
public sealed partial class DomBridge
{
    private JSObject? ResolveCurrentWindow() => _windowContext.ResolveCurrentWindow();

    private JSObject? ResolveOwnerWindow(JSObject target) => _windowContext.ResolveOwnerWindow(target);

    private JSObject? GetCanonicalWindow(JSObject? candidate) => _windowContext.GetCanonicalWindow(candidate);

    private void RunWithWindowContext(JSObject targetWindow, Action callback) =>
        _windowContext.RunWithWindowContext(targetWindow, callback);

    private JSValue GetWindowDocument(JSObject targetWindow) => _windowContext.GetWindowDocument(targetWindow);

    private JSValue GetWindowParent(JSObject targetWindow) => _windowContext.GetWindowParent(targetWindow);
}
