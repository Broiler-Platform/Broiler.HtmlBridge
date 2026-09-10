using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Logging;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IDialogHost"/>, the narrow contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.DialogBinding"/> feature module consumes
/// (HtmlBridge complexity-reduction roadmap Phase 3, P3.7). Each member is an explicit interface
/// implementation, so these seams do not widen the public <c>DomBridge</c> surface. The dialog/
/// popover runtime state still lives on the per-element <see cref="ElementRuntimeState"/> tables and
/// the top-layer counter; these accessors are the single point a future TopLayerManager re-homes.
/// </summary>
/// <remarks>
/// This is the half-migrated seam for the dialog slice: the module speaks JSEAL, and the one member
/// that has to hand an object to the unmigrated half of the bridge —
/// <see cref="IDialogHost.DispatchFullscreenChange"/>, whose dispatcher still takes an engine object
/// — builds it through the realm and crosses with <see cref="Dom.Runtime.JsInterop"/>. That is a cast
/// and not a conversion: a JSEAL object handle carries the engine's own object.
/// </remarks>
public sealed partial class DomBridge : IDialogHost
{
    IJsRealm IDialogHost.Realm => Realm;

    void IDialogHost.SetOpenAttribute(DomElement element, bool open)
    {
        if (open)
            SetAttr(element, "open", "");
        else
            RemoveAttr(element, "open");
    }

    bool IDialogHost.HasOpenAttribute(DomElement element) => HasAttr(element, "open");

    void IDialogHost.InvalidateStyleScope(DomElement element) => InvalidateStyleScope(element);

    void IDialogHost.AssignNextTopLayerOrder(DomElement element) =>
        DialogStateFor(element).TopLayerOrder.Set(++_topLayerCounter);

    void IDialogHost.SetDialogModal(DomElement element, bool modal)
    {
        if (modal)
            DialogStateFor(element).Modal.Set(true);
        else
            DialogStateFor(element).Modal.Remove();
    }

    void IDialogHost.SetPopoverOpen(DomElement element, bool open)
    {
        if (open)
        {
            DialogStateFor(element).PopoverOpen.Set(true);
            // A fresh show clears any leftover "transitioning out" mark: if the element is now
            // transitioning `overlay` at all, it is transitioning *in*.
            DialogStateFor(element).PopoverTransitioningOut.Remove();
        }
        else
        {
            DialogStateFor(element).PopoverOpen.Remove();
        }
    }

    void IDialogHost.SetFullscreen(DomElement element, bool fullscreen)
    {
        if (fullscreen)
            DialogStateFor(element).Fullscreen.Set(true);
        else
            DialogStateFor(element).Fullscreen.Remove();
    }

    DomElement? IDialogHost.GetFullscreenElement() => FindFullscreenElement();

    void IDialogHost.DispatchFullscreenChange(DomElement target)
    {
        try
        {
            // The event is built through the realm and unwrapped for the dispatcher, which still
            // takes an engine object. Both halves name the same realm, so the object the listener
            // sees is the one this built.
            var evt = Realm.NewObject();
            Realm.DefineValue(evt, "type", JsValue.String("fullscreenchange"));
            Realm.DefineValue(evt, "bubbles", JsValue.True);
            _eventDispatch.DispatchEventOnElement(target, evt);
        }
        catch (Exception ex)
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.DispatchFullscreenChange",
                $"fullscreenchange handler error: {ex.Message}", ex);
        }
    }

    void IDialogHost.MarkPopoverOverlayTransitioningOut(DomElement element) =>
        DialogStateFor(element).PopoverTransitioningOut.Set(true);

    string IDialogHost.GetReturnValue(DomElement element) =>
        FormControlStateFor(element).ReturnValue.TryGet(out var rv) && rv is string s
            ? s
            : string.Empty;

    void IDialogHost.SetReturnValue(DomElement element, string value) =>
        FormControlStateFor(element).ReturnValue.Set(value);

    bool IDialogHost.PopoverKeepsOverlayOnHide(DomElement element) => PopoverKeepsOverlayOnHide(element);

    bool IDialogHost.DialogKeepsOverlayOnClose(DomElement element) => DialogKeepsOverlayOnClose(element);

    bool IDialogHost.DialogKeepsDisplayOnClose(DomElement element) => DialogKeepsDisplayOnClose(element);
}
