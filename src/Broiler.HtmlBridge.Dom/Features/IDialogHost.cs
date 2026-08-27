using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow bridge services the <see cref="DialogBinding"/> feature module needs (HtmlBridge
/// complexity-reduction roadmap Phase 3, P3.7). The dialog/popover JS API sets the element's
/// <c>open</c> attribute and a small amount of per-element browser-runtime state (modal flag,
/// popover-open flag, top-layer order, dialog return value) that today lives on the bridge's
/// <c>ElementRuntimeState</c>, and asks the renderer whether a hiding popover keeps its overlay.
/// These are exposed as named primitives so the feature module never reaches the runtime-state
/// object directly; the eventual TopLayerManager can re-home the state behind the same contract.
/// Backdrop/top-layer rendering stays in the bridge.
/// </summary>
internal interface IDialogHost
{
    /// <summary>Adds (<paramref name="open"/> true) or removes the boolean <c>open</c> attribute.</summary>
    void SetOpenAttribute(DomElement element, bool open);

    /// <summary>Whether <paramref name="element"/> currently has the <c>open</c> attribute.</summary>
    bool HasOpenAttribute(DomElement element);

    /// <summary>Invalidates the element's style scope so open/top-layer changes re-cascade.</summary>
    void InvalidateStyleScope(DomElement element);

    /// <summary>Assigns <paramref name="element"/> the next monotonic top-layer order (promotes it
    /// above previously promoted dialogs/popovers).</summary>
    void AssignNextTopLayerOrder(DomElement element);

    /// <summary>Sets or clears the dialog's modal flag.</summary>
    void SetDialogModal(DomElement element, bool modal);

    /// <summary>Sets or clears the element's popover-open flag.</summary>
    void SetPopoverOpen(DomElement element, bool open);

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

    /// <summary>Whether a hiding popover must stay in the top layer (mid-transition overlay per
    /// CSS Position §overlay) — a renderer decision.</summary>
    bool PopoverKeepsOverlayOnHide(DomElement element);

    /// <summary>Whether a closing dialog must stay in the top layer because its <c>overlay</c> is
    /// transitioned with <c>allow-discrete</c> — the dialog counterpart of
    /// <see cref="PopoverKeepsOverlayOnHide"/> (CSS Position §overlay).</summary>
    bool DialogKeepsOverlayOnClose(DomElement element);

    /// <summary>Whether a closing dialog must keep generating a box because its <c>display</c> is
    /// transitioned with <c>allow-discrete</c>, so the UA sheet's
    /// <c>dialog:not([open]) { display: none }</c> must not take effect yet.</summary>
    bool DialogKeepsDisplayOnClose(DomElement element);

    /// <summary>Records that <c>hidePopover()</c> left the element in the top layer because its
    /// <c>overlay</c> is transitioning out, so the show-time "held out of the top layer while
    /// <c>overlay</c> transitions in" rule does not misfire on it (CSS Position §overlay).</summary>
    void MarkPopoverOverlayTransitioningOut(DomElement element);
}
