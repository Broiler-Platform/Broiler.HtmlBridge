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
/// <b>The owner speaks JSEAL and so, now, does everything here except one overload.</b> Two of these
/// delegators used to answer the engine's own object while both the manager below them and the
/// messaging host above them held handles, so a window was converted out and converted straight back
/// for no reader. Those are gone. What remains engine-typed is
/// <see cref="RunWithWindowContext(JSObject, Action)"/>, whose caller is the sub-document script
/// runner in <c>DomBridge/SubDocuments.cs</c> — another group's file, and its turn is later.
/// </para>
/// <para>
/// The <see cref="RunWithWindowContext(JsValue, Action)"/> overload is the same call for a caller that
/// already holds a handle (<c>DomBridge.SubDocumentGlobals.cs</c>), and exists so that caller does not
/// unwrap only for this file to wrap again.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>
    /// A window handle, or <see cref="JsValue.Null"/> when the manager answered "no window".
    /// </summary>
    /// <remarks>
    /// <b>The CLR null these forms used to answer was the same answer spelled in a type only the
    /// middle of this path used.</b> The manager speaks JSEAL and so does <c>IMessagingHost</c>;
    /// these two delegators converted a handle to an engine object on the way out and the host
    /// converted it straight back, which named one window either way. What is kept is the
    /// distinction that conversion carried: a non-object answer becomes <c>null</c> for the page,
    /// not <see cref="JsValue.Missing"/>, because "no window" is a value a page reads.
    /// </remarks>
    private static JsValue WindowOrNull(JsValue window) => window.IsObject ? window : JsValue.Null;

    private JsValue ResolveCurrentWindow() => WindowOrNull(_windowContext.ResolveCurrentWindow());

    private JsValue ResolveOwnerWindow(JsValue target) =>
        WindowOrNull(_windowContext.ResolveOwnerWindow(target));

    private JsValue GetCanonicalWindow(JsValue candidate) => _windowContext.GetCanonicalWindow(candidate);

    private void RunWithWindowContext(JSObject targetWindow, Action callback) =>
        _windowContext.RunWithWindowContext(Dom.Runtime.JsInterop.FromEngineObject(targetWindow), callback);

    private void RunWithWindowContext(JsValue targetWindow, Action callback) =>
        _windowContext.RunWithWindowContext(targetWindow, callback);

    private JsValue GetWindowDocument(JsValue targetWindow) => _windowContext.GetWindowDocument(targetWindow);

    private JsValue GetWindowParent(JsValue targetWindow) => _windowContext.GetWindowParent(targetWindow);
}
