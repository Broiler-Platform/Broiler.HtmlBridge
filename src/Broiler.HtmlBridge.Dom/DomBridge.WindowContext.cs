using System;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

/// <summary>
/// Thin bridge delegators for the browsing-context window-resolution behaviour, which now lives in the
/// single <see cref="Broiler.HtmlBridge.Dom.Runtime.WindowContextManager"/> owner (HtmlBridge
/// complexity-reduction roadmap Phase 3, P3.18 — the last Frames residue; the owner reads the sub-window
/// state from the P3.16 <c>BrowsingContextManager</c>). These forwarders keep the callers unchanged: the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.MessagingBinding"/> reaches them through the
/// <see cref="Broiler.HtmlBridge.Dom.Features.IMessagingHost"/> contract, and the sub-document script
/// runner calls <c>RunWithWindowContext</c> directly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every delegator here speaks JSEAL now, and the last one that did not was held open by a
/// comment naming a caller it did not have.</b> Several of these used to take or answer the engine's
/// own object while both the manager below them and the callers above them held handles, so a window
/// was converted out and converted straight back for no reader. The last of them was a second
/// <c>RunWithWindowContext</c> overload taking the engine's object, and the remarks that kept it said
/// its caller was the sub-document script runner in <c>DomBridge/SubDocuments.cs</c>. It was not:
/// that call site is one unchanged line, and it has bound to the overload below ever since
/// <c>SubWindowBinding.GetOrCreate</c> was re-typed to answer a handle — which
/// <c>FrameScriptExecutionTests</c> states in its own header, having been written to cover exactly
/// that rebinding.
/// </para>
/// <para>
/// The only caller the deleted overload had was <c>DomBridge.MessagingHost.cs</c>, which unwrapped a
/// handle to an engine object so that the overload could wrap the same object straight back before
/// handing it to the manager, whose own <c>RunWithWindowContext</c> has taken a handle all along.
/// All three callers of <see cref="RunWithWindowContext(JsValue, Action)"/> — that host,
/// <c>DomBridge.SubDocumentGlobals.cs</c> and <c>DomBridge/SubDocuments.cs</c> — already hold
/// one, so there is nothing left for an engine-typed spelling to save any of them.
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

    private void RunWithWindowContext(JsValue targetWindow, Action callback) =>
        _windowContext.RunWithWindowContext(targetWindow, callback);

    private JsValue GetWindowDocument(JsValue targetWindow) => _windowContext.GetWindowDocument(targetWindow);

    private JsValue GetWindowParent(JsValue targetWindow) => _windowContext.GetWindowParent(targetWindow);
}
