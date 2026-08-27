using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.HtmlBridge.Logging;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IDialogHost"/>, the narrow contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.DialogBinding"/> feature module consumes
/// (HtmlBridge complexity-reduction roadmap Phase 3, P3.7). Each member is an explicit interface
/// implementation, so these seams do not widen the public <c>DomBridge</c> surface. The dialog/
/// popover runtime state still lives on the per-element <see cref="ElementRuntimeState"/> tables and
/// the top-layer counter; these accessors are the single point a future TopLayerManager re-homes.
/// </summary>
public sealed partial class DomBridge : IDialogHost
{
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
            var evt = new JSObject();
            evt.FastAddValue("type", new JSString("fullscreenchange"),
                JSPropertyAttributes.EnumerableConfigurableValue);
            evt.FastAddValue("bubbles", JSBoolean.True,
                JSPropertyAttributes.EnumerableConfigurableValue);
            DispatchEventOnElement(target, evt);
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
