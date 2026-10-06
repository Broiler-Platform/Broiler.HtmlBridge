using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow bridge services the <see cref="DialogBinding"/> feature module needs. The
/// dialog/popover JS API sets the element's
/// <c>open</c> attribute and a small amount of per-element browser-runtime state (modal flag,
/// popover-open flag, top-layer order, dialog return value) that lives in the bridge's per-element
/// <c>DialogRuntimeState</c> table (the return value in its <c>FormControlRuntimeState</c>), and asks
/// the renderer whether a hiding popover keeps its overlay. These are exposed as named primitives so
/// the feature module never reaches the runtime-state object directly; the eventual TopLayerManager
/// can re-home the state behind the same contract. Backdrop/top-layer rendering stays in the bridge.
/// </summary>
/// <remarks>
/// The realm inherited from <see cref="IRealmHost"/> is also the one whose job queue backs the
/// resolved promise the fullscreen methods return.
/// </remarks>
internal interface IDialogHost : IRealmHost, IStyleInvalidationHost
{
    /// <summary>Adds (<paramref name="open"/> true) or removes the boolean <c>open</c> attribute.</summary>
    void SetOpenAttribute(DomElement element, bool open);

    /// <summary>Whether <paramref name="element"/> currently has the <c>open</c> attribute.</summary>
    bool HasOpenAttribute(DomElement element);

    /// <summary>Whether <paramref name="element"/> is open as a modal dialog.</summary>
    bool IsDialogModal(DomElement element);

    /// <summary>
    /// Fires the trusted, non-bubbling <paramref name="type"/> at <paramref name="element"/> -- a
    /// <c>ToggleEvent</c> carrying <paramref name="oldState"/> and <paramref name="newState"/> when they are
    /// given -- and answers whether it was not cancelled.
    /// </summary>
    bool FireDialogEvent(DomElement element, string type, bool cancelable, string? oldState = null, string? newState = null);

    /// <summary>
    /// Queues the element's <c>toggle</c> (HTML's toggle task tracker): one queued already is cancelled, its
    /// old state kept, and the new one goes to the back of the queue.
    /// </summary>
    void QueueToggleEvent(DomElement element, string oldState, string newState);

    /// <summary>Whether <paramref name="element"/> is a popover that is showing.</summary>
    bool IsPopoverShowing(DomElement element);

    /// <summary>Closes the open auto popovers a modal dialog that is showing is not in, with their events.</summary>
    void HidePopoversForModalDialog(DomElement dialog);

    /// <summary>
    /// The dialog focusing steps, as Chromium takes them (measured): focus to an <c>autofocus</c> element in
    /// the dialog, else its first focusable one, else the dialog; what had focus is remembered.
    /// </summary>
    void RunDialogFocusingSteps(DomElement dialog);

    /// <summary>Gives focus back to what had it before the dialog opened, when focus is in it or it was modal.</summary>
    void RestoreFocusAfterDialog(DomElement dialog, bool wasModal);

    /// <summary>Queues <paramref name="action"/> for the next animation frame.</summary>
    void QueueDialogFrameAction(Action action);

    /// <summary>Runs <paramref name="call"/>, a script's call: the events it fires end with no microtask checkpoint.</summary>
    JsValue RunAsScriptCall(Func<JsValue> call);

    /// <summary>Assigns <paramref name="element"/> the next monotonic top-layer order (promotes it
    /// above previously promoted dialogs/popovers).</summary>
    void AssignNextTopLayerOrder(DomElement element);

    /// <summary>Sets or clears the dialog's modal flag.</summary>
    void SetDialogModal(DomElement element, bool modal);

    /// <summary>
    /// Sets or clears the element's fullscreen flag (Fullscreen §
    /// <c>requestFullscreen</c>/<c>exitFullscreen</c>). A fullscreen element joins the top layer
    /// and generates a <c>::backdrop</c>, the same machinery a modal dialog uses.
    /// </summary>
    void SetFullscreen(DomElement element, bool fullscreen);

    /// <summary>The document's current fullscreen element, or <c>null</c> when there is none.</summary>
    DomElement? GetFullscreenElement();

    /// <summary>
    /// Fires <c>fullscreenchange</c> at <paramref name="target"/>. The event bubbles, so a listener
    /// on the document sees an element's transition — which is how the WPT fullscreen reftests
    /// clear their <c>reftest-wait</c> class.
    /// </summary>
    void DispatchFullscreenChange(DomElement target);

    /// <summary>The dialog's current <c>returnValue</c> (empty string if unset).</summary>
    string GetReturnValue(DomElement element);

    /// <summary>Sets the dialog's <c>returnValue</c>.</summary>
    void SetReturnValue(DomElement element, string value);

    /// <summary>Whether a closing dialog must stay in the top layer because its <c>overlay</c> is
    /// transitioned with <c>allow-discrete</c>, as a hiding popover does (CSS Position §overlay).</summary>
    bool DialogKeepsOverlayOnClose(DomElement element);

    /// <summary>Whether a closing dialog must keep generating a box because its <c>display</c> is
    /// transitioned with <c>allow-discrete</c>, so the UA sheet's
    /// <c>dialog:not([open]) { display: none }</c> must not take effect yet.</summary>
    bool DialogKeepsDisplayOnClose(DomElement element);
}
