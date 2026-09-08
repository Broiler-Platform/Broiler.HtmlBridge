using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

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
/// <remarks>
/// <para>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>): the members are minted by the realm
/// and their bodies run on a <see cref="JsCall"/>, so the migrated half of this file names no engine
/// type.
/// </para>
/// <para>
/// <b>Two members stay engine-typed, and both are pinned from outside.</b>
/// <see cref="Install(JSObject, DomElement, string, bool)"/> is called by
/// <c>DomBridge/ElementInterfaces.cs</c> with an engine wrapper, and
/// <see cref="InstallElementMembers"/> by <c>DomBridge/ElementInterface.cs</c> with an
/// <see cref="ElementSource"/> — a delegate whose parameter <em>is</em> the engine's argument frame,
/// so a JSEAL callback has nothing to hand it. <see cref="ExitFullscreen"/> is the third: its caller
/// is <c>DomBridge/Registration/Document.cs</c>, which takes an engine value back. Each forwards
/// through <see cref="Runtime.JsInterop"/>, which carries an object across without converting it, and
/// each disappears when its caller migrates.
/// </para>
/// </remarks>
internal sealed class DialogBinding(IDialogHost host)
{
    private readonly IDialogHost _host = host;

    /// <summary>
    /// Fullscreen's <c>Element.requestFullscreen()</c> and its <c>webkit</c> alias — on
    /// <c>Element.prototype</c>, since the Fullscreen API extends <c>Element</c> rather than any tag
    /// (Chromium has both there too).
    /// </summary>
    /// <remarks>
    /// Engine-typed because <see cref="ElementSource"/> is: the receiver-resolving source reads the
    /// call's <c>this</c> out of the engine's own argument frame, so the member it serves has to be
    /// installed with that frame. Only the wrapping is engine-typed — the operation itself is
    /// <see cref="RequestFullscreen"/>, which speaks JSEAL.
    /// </remarks>
    internal void InstallElementMembers(JSObject target, ElementSource element)
    {
        target.FastAddValue("requestFullscreen",
            new DomFunction(
                (in a) => Runtime.JsInterop.ToEngineObject(RequestFullscreen(element(in a, "requestFullscreen"))),
                "requestFullscreen", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);
        target.FastAddValue("webkitRequestFullscreen",
            new DomFunction(
                (in a) => Runtime.JsInterop.ToEngineObject(RequestFullscreen(element(in a, "webkitRequestFullscreen"))),
                "webkitRequestFullscreen", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);
    }

    /// <summary>
    /// Engine-typed adapter for <c>DomBridge/ElementInterfaces.cs</c>, which still holds the element
    /// wrapper as an engine object. See the remarks on this class.
    /// </summary>
    internal void Install(JSObject obj, DomElement element, string tag, bool hasPopover) =>
        Install(Runtime.JsInterop.FromEngineObject(obj), element, tag, hasPopover);

    /// <summary>
    /// Installs the dialog/details interface members and the popover methods on
    /// <paramref name="obj"/> for <paramref name="element"/> (by <paramref name="tag"/> for
    /// dialog/details; by <paramref name="hasPopover"/> for the tag-agnostic popover API).
    /// </summary>
    internal void Install(JsValue obj, DomElement element, string tag, bool hasPopover)
    {
        var realm = _host.Realm;

        if (tag == "details")
        {
            realm.DefineAccessor(obj, "open",
                (in _) => JsValue.Boolean(_host.HasOpenAttribute(element)),
                (in call) => SetOpenState(element, in call));
        }

        if (tag == "dialog")
        {
            realm.DefineValue(obj, "showModal", realm.NewMethod("showModal", (in _) => ShowModal(element), 0));
            realm.DefineValue(obj, "show", realm.NewMethod("show", (in _) => Show(element), 0));
            realm.DefineValue(obj, "close", realm.NewMethod("close", (in call) => Close(element, in call), 1));
            realm.DefineAccessor(obj, "open",
                (in _) => JsValue.Boolean(_host.HasOpenAttribute(element)),
                (in call) => SetOpenState(element, in call));
            realm.DefineAccessor(obj, "returnValue",
                (in _) => JsValue.String(_host.GetReturnValue(element)),
                (in call) => SetReturnValue(element, in call));
        }

        // Popover API (HTML §popover) — showPopover()/hidePopover() are exposed on any element
        // carrying the global `popover` attribute, not tied to a tag.
        if (hasPopover)
        {
            realm.DefineValue(obj, "showPopover", realm.NewMethod("showPopover", (in _) => ShowPopover(element), 0));
            realm.DefineValue(obj, "hidePopover", realm.NewMethod("hidePopover", (in _) => HidePopover(element), 0));
        }
    }

    // details.open = value / dialog.open = value — reflect the boolean open attribute.
    private JsValue SetOpenState(DomElement element, in JsCall call)
    {
        _host.SetOpenAttribute(element, call[0].AsBoolean);
        _host.InvalidateStyleScope(element);
        return JsValue.Undefined;
    }

    /// <summary>
    /// An already-resolved promise, the return value of the fullscreen methods. Both complete
    /// synchronously here — there is no compositor step to wait on — so the promise exists only so
    /// that <c>requestFullscreen().then(…)</c> works.
    /// </summary>
    /// <remarks>
    /// The realm hands back the promise and its two settling functions rather than taking an executor
    /// (see <see cref="IJsJobs.NewPromise"/>), so resolving it is a call rather than a completed
    /// <c>Task</c> handed to the engine's own adapter. The observable result is the same object a page
    /// saw before: a promise that is already fulfilled with <c>undefined</c>.
    /// </remarks>
    private JsValue ResolvedPromise()
    {
        var promise = _host.Realm.NewPromise(out var resolve, out _);
        resolve(JsValue.Undefined);
        return promise;
    }

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
    internal JsValue RequestFullscreen(DomElement element)
    {
        _host.SetFullscreen(element, true);
        _host.AssignNextTopLayerOrder(element);
        _host.InvalidateStyleScope(element);
        _host.DispatchFullscreenChange(element);
        return ResolvedPromise();
    }

    /// <summary>
    /// Engine-typed adapter for <c>DomBridge/Registration/Document.cs</c>, which installs
    /// <c>document.exitFullscreen</c> with an engine function and so takes an engine value back. See
    /// the remarks on this class.
    /// </summary>
    internal JSObject ExitFullscreen() => Runtime.JsInterop.ToEngineObject(ExitFullscreenCore());

    /// <summary>
    /// Fullscreen §<c>exitFullscreen()</c>: takes the document's fullscreen element back out of the
    /// top layer and fires <c>fullscreenchange</c> at it. A no-op when nothing is fullscreen.
    /// </summary>
    internal JsValue ExitFullscreenCore()
    {
        if (_host.GetFullscreenElement() is not { } element)
            return ResolvedPromise();

        _host.SetFullscreen(element, false);
        _host.InvalidateStyleScope(element);
        _host.DispatchFullscreenChange(element);
        return ResolvedPromise();
    }

    private JsValue ShowModal(DomElement element)
    {
        _host.SetOpenAttribute(element, true);
        _host.SetDialogModal(element, true);
        _host.AssignNextTopLayerOrder(element);
        _host.InvalidateStyleScope(element);
        return JsValue.Undefined;
    }

    private JsValue Show(DomElement element)
    {
        _host.SetOpenAttribute(element, true);
        _host.InvalidateStyleScope(element);
        return JsValue.Undefined;
    }

    // showPopover() promotes the element to the top layer (so its ::backdrop renders), modeled with
    // the same runtime flag + top-layer order the modal-dialog path uses.
    private JsValue ShowPopover(DomElement element)
    {
        _host.SetPopoverOpen(element, true);
        _host.AssignNextTopLayerOrder(element);
        _host.InvalidateStyleScope(element);
        return JsValue.Undefined;
    }

    private JsValue HidePopover(DomElement element)
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
        return JsValue.Undefined;
    }

    private JsValue Close(DomElement element, in JsCall call)
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
        if (call.Length > 0)
            // ToJsString, not the handle's rendering: `close(obj)` stores what the object's own
            // toString answers, which is the coercion a page observes on `dialog.returnValue`.
            _host.SetReturnValue(element, call.Realm.ToJsString(call[0]));
        _host.InvalidateStyleScope(element);
        return JsValue.Undefined;
    }

    private JsValue SetReturnValue(DomElement element, in JsCall call)
    {
        _host.SetReturnValue(element, call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty);
        return JsValue.Undefined;
    }
}
