using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.Promise;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The dialog / popover / details JS API feature binding (HtmlBridge complexity-reduction roadmap
/// Phase 3, P3.7) — <c>HTMLDialogElement</c> (<c>showModal</c>/<c>show</c>/<c>close</c>/<c>open</c>/
/// <c>returnValue</c>), the popover API (<c>showPopover</c>/<c>hidePopover</c> on any element with
/// the global <c>popover</c> attribute) and <c>HTMLDetailsElement.open</c>. It drives the element's
/// <c>open</c> attribute and the modal/popover/top-layer/return-value runtime state through the
/// narrow <see cref="IDialogHost"/> contract; the backdrop/top-layer <em>rendering</em> stays in the
/// bridge's anchor resolver.
/// </summary>
internal sealed class DialogBinding(IDialogHost host)
{
    private readonly IDialogHost _host = host;

    /// <summary>
    /// Fullscreen's <c>Element.requestFullscreen()</c> and its <c>webkit</c> alias — on
    /// <c>Element.prototype</c>, since the Fullscreen API extends <c>Element</c> rather than any tag
    /// (Chromium has both there too).
    /// </summary>
    internal void InstallElementMembers(JSObject target, ElementSource element)
    {
        target.FastAddValue("requestFullscreen",
            new DomFunction((in a) => RequestFullscreen(element(in a, "requestFullscreen")), "requestFullscreen", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);
        target.FastAddValue("webkitRequestFullscreen",
            new DomFunction((in a) => RequestFullscreen(element(in a, "webkitRequestFullscreen")), "webkitRequestFullscreen", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);
    }

    /// <summary>
    /// Installs the dialog/details interface members and the popover methods on
    /// <paramref name="obj"/> for <paramref name="element"/> (by <paramref name="tag"/> for
    /// dialog/details; by <paramref name="hasPopover"/> for the tag-agnostic popover API).
    /// </summary>
    internal void Install(JSObject obj, DomElement element, string tag, bool hasPopover)
    {
        if (tag == "details")
        {
            obj.FastAddProperty("open",
                new DomFunction((in _) => _host.HasOpenAttribute(element) ? JSBoolean.True : JSBoolean.False, "get open"),
                new DomFunction((in a) => SetOpenState(element, in a), "set open"),
                JSPropertyAttributes.EnumerableConfigurableProperty);
        }

        if (tag == "dialog")
        {
            obj.FastAddValue("showModal", new DomFunction((in _) => ShowModal(element), "showModal", 0), JSPropertyAttributes.EnumerableConfigurableValue);
            obj.FastAddValue("show", new DomFunction((in _) => Show(element), "show", 0), JSPropertyAttributes.EnumerableConfigurableValue);
            obj.FastAddValue("close", new DomFunction((in a) => Close(element, in a), "close", 1), JSPropertyAttributes.EnumerableConfigurableValue);
            obj.FastAddProperty("open",
                new DomFunction((in _) => _host.HasOpenAttribute(element) ? JSBoolean.True : JSBoolean.False, "get open"),
                new DomFunction((in a) => SetOpenState(element, in a), "set open"),
                JSPropertyAttributes.EnumerableConfigurableProperty);
            obj.FastAddProperty("returnValue",
                new DomFunction((in _) => new JSString(_host.GetReturnValue(element)), "get returnValue"),
                new DomFunction((in a) => SetReturnValue(element, in a), "set returnValue"),
                JSPropertyAttributes.EnumerableConfigurableProperty);
        }

        // Popover API (HTML §popover) — showPopover()/hidePopover() are exposed on any element
        // carrying the global `popover` attribute, not tied to a tag.
        if (hasPopover)
        {
            obj.FastAddValue("showPopover", new DomFunction((in _) => ShowPopover(element), "showPopover", 0), JSPropertyAttributes.EnumerableConfigurableValue);
            obj.FastAddValue("hidePopover", new DomFunction((in _) => HidePopover(element), "hidePopover", 0), JSPropertyAttributes.EnumerableConfigurableValue);
        }
    }

    // details.open = value / dialog.open = value — reflect the boolean open attribute.
    private JSValue SetOpenState(DomElement element, in Arguments a)
    {
        _host.SetOpenAttribute(element, a.Length > 0 && a[0].BooleanValue);
        _host.InvalidateStyleScope(element);
        return JSUndefined.Value;
    }

    /// <summary>
    /// An already-resolved promise, the return value of the fullscreen methods. Both complete
    /// synchronously here — there is no compositor step to wait on — so the promise exists only so
    /// that <c>requestFullscreen().then(…)</c> works.
    /// </summary>
    private static JSValue ResolvedPromise() => Task.CompletedTask.ToPromise();

    /// <summary>
    /// Fullscreen §<c>requestFullscreen()</c>: promotes the element into the top layer, where the
    /// UA geometry sizes it to the viewport and it generates a <c>::backdrop</c>, then fires
    /// <c>fullscreenchange</c>. Returns a resolved promise.
    /// </summary>
    /// <remarks>
    /// Two things a browser does that this deliberately does not. There is no transient
    /// user-activation check — the runner has no user, and the WPT tests reach this through
    /// <c>test_driver.bless</c>, whose whole job is to stand in for that activation. And the
    /// element stack is flattened to a single element: nesting fullscreen requests is not something
    /// the reftests exercise, and <see cref="IDialogHost.GetFullscreenElement"/> resolves ties by
    /// top-layer order, so the most recent request wins.
    /// </remarks>
    internal JSValue RequestFullscreen(DomElement element)
    {
        _host.SetFullscreen(element, true);
        _host.AssignNextTopLayerOrder(element);
        _host.InvalidateStyleScope(element);
        _host.DispatchFullscreenChange(element);
        return ResolvedPromise();
    }

    /// <summary>
    /// Fullscreen §<c>exitFullscreen()</c>: takes the document's fullscreen element back out of the
    /// top layer and fires <c>fullscreenchange</c> at it. A no-op when nothing is fullscreen.
    /// </summary>
    internal JSValue ExitFullscreen()
    {
        if (_host.GetFullscreenElement() is not { } element)
            return ResolvedPromise();

        _host.SetFullscreen(element, false);
        _host.InvalidateStyleScope(element);
        _host.DispatchFullscreenChange(element);
        return ResolvedPromise();
    }

    private JSValue ShowModal(DomElement element)
    {
        _host.SetOpenAttribute(element, true);
        _host.SetDialogModal(element, true);
        _host.AssignNextTopLayerOrder(element);
        _host.InvalidateStyleScope(element);
        return JSUndefined.Value;
    }

    private JSValue Show(DomElement element)
    {
        _host.SetOpenAttribute(element, true);
        _host.InvalidateStyleScope(element);
        return JSUndefined.Value;
    }

    // showPopover() promotes the element to the top layer (so its ::backdrop renders), modeled with
    // the same runtime flag + top-layer order the modal-dialog path uses.
    private JSValue ShowPopover(DomElement element)
    {
        _host.SetPopoverOpen(element, true);
        _host.AssignNextTopLayerOrder(element);
        _host.InvalidateStyleScope(element);
        return JSUndefined.Value;
    }

    private JSValue HidePopover(DomElement element)
    {
        // CSS Position §overlay: hiding a popover whose `overlay` is transitioned with
        // `transition-behavior: allow-discrete` keeps it in the top layer for the duration of the
        // transition. A static render snapshots mid-transition, so the popover (and its ::backdrop)
        // must stay rendered — leave the flag set. Without such a transition it hides immediately.
        if (_host.PopoverKeepsOverlayOnHide(element))
            _host.MarkPopoverOverlayTransitioningOut(element);
        else
            _host.SetPopoverOpen(element, false);
        _host.InvalidateStyleScope(element);
        return JSUndefined.Value;
    }

    private JSValue Close(DomElement element, in Arguments a)
    {
        // CSS Position §overlay: closing a dialog whose `overlay` is transitioned with
        // `transition-behavior: allow-discrete` keeps it in the top layer for the transition's
        // duration, exactly as HidePopover already does for a popover — a static render snapshots
        // mid-transition, so the dialog and its ::backdrop must stay rendered. `display` is the
        // separate half: the UA sheet's `dialog:not([open]) { display: none }` is what decides
        // whether a box is generated, so the `open` attribute survives only while `display` is
        // itself mid-discrete-transition. A dialog that transitions `overlay` alone is still in the
        // top layer but generates no box, which is what the spec asks for.
        //
        // Like the popover path, this leaves `dialog.open` reading true for the transition's
        // duration where the spec clears it synchronously. Only a dialog that declares the discrete
        // `display` transition is affected, which is exactly the mid-transition snapshot case.
        if (!_host.DialogKeepsDisplayOnClose(element))
            _host.SetOpenAttribute(element, false);
        if (!_host.DialogKeepsOverlayOnClose(element))
            _host.SetDialogModal(element, false);
        if (a.Length > 0)
            _host.SetReturnValue(element, a[0].ToString());
        _host.InvalidateStyleScope(element);
        return JSUndefined.Value;
    }

    private JSValue SetReturnValue(DomElement element, in Arguments a)
    {
        _host.SetReturnValue(element, a.Length > 0 ? a[0].ToString() : string.Empty);
        return JSUndefined.Value;
    }
}
