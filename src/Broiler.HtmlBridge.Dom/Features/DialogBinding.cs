using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The dialog / popover / details JS API feature binding —
/// <c>HTMLDialogElement</c> (<c>showModal</c>/<c>show</c>/<c>close</c>/<c>open</c>/
/// <c>returnValue</c>), the popover API (<c>showPopover</c>/<c>hidePopover</c> on any element with
/// the global <c>popover</c> attribute) and <c>HTMLDetailsElement.open</c>. It drives the element's
/// <c>open</c> attribute and the modal/popover/top-layer/return-value runtime state through the
/// narrow <see cref="IDialogHost"/> contract; the backdrop/top-layer <em>rendering</em> stays in the
/// bridge's anchor resolver.
/// </summary>
/// <remarks>
/// <para>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>) throughout: every member is minted by
/// the realm and every body runs on a <see cref="JsCall"/>, so this file names no engine type at all.
/// </para>
/// </remarks>
internal sealed class DialogBinding(IDialogHost host)
{
    private readonly IDialogHost _host = host;

    // The toggle event each dialog has queued and not fired yet: HTML's "queue a dialog toggle event task"
    // keeps one per dialog, its first old state and its last new state (measured: show() and close() in
    // one task fire a single toggle, closed -> closed).
    private readonly Dictionary<DomElement, (string OldState, string NewState)> _pendingToggles = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Fullscreen's <c>Element.requestFullscreen()</c> and its <c>webkit</c> alias — on
    /// <c>Element.prototype</c>, since the Fullscreen API extends <c>Element</c> rather than any tag
    /// (Chromium has both there too).
    /// </summary>
    /// <remarks>
    /// Both are the realm's, minted with the same attributes the engine pair they replace installed —
    /// enumerable and configurable, which is <see cref="JsPropertyFlags.Default"/> for a value — and in
    /// the same order, so <c>Object.getOwnPropertyNames(Element.prototype)</c> reads unchanged. The
    /// operation itself was already JSEAL: <see cref="RequestFullscreen"/> hands back the promise
    /// directly now instead of being unwrapped for an engine return.
    /// </remarks>
    internal void InstallElementMembers(JsValue target, JsElementSource element)
    {
        var realm = _host.Realm;

        realm.DefineMethod(target, "requestFullscreen", 0,
            (in call) => RequestFullscreen(element(in call, "requestFullscreen")));
        realm.DefineMethod(target, "webkitRequestFullscreen", 0,
            (in call) => RequestFullscreen(element(in call, "webkitRequestFullscreen")));
    }

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
            realm.DefineMethod(obj, "showModal", 0, (in call) => ShowModal(element, call.Realm));
            realm.DefineMethod(obj, "show", 0, (in call) => Show(element, call.Realm));
            realm.DefineMethod(obj, "close", 1, (in call) => Close(element, in call));
            realm.DefineMethod(obj, "requestClose", 1, (in call) =>
            {
                string? returnValue = call.Length > 0 && !call[0].IsUndefined ? call.Realm.ToJsString(call[0]) : null;
                return _host.RunAsScriptCall(() =>
                {
                    RequestClose(element, returnValue);
                    return JsValue.Undefined;
                });
            });
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
            realm.DefineMethod(obj, "showPopover", 0, (in _) => ShowPopover(element));
            realm.DefineMethod(obj, "hidePopover", 0, (in _) => HidePopover(element));
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

    /// <summary>
    /// <c>showModal()</c>, as HTML and Chromium have it (measured): an <c>InvalidStateError</c> for a dialog
    /// open as a non-modal one or out of a document, nothing for one already modal; otherwise a cancelable
    /// <c>beforetoggle</c>, then the dialog opens in the top layer and its <c>toggle</c> follows as a task.
    /// </summary>
    private JsValue ShowModal(DomElement element, IJsRealm realm)
    {
        if (_host.HasOpenAttribute(element))
        {
            if (_host.IsDialogModal(element))
                return JsValue.Undefined;

            throw realm.DomError("InvalidStateError",
                "Failed to execute 'showModal' on 'HTMLDialogElement': The dialog is already open as a non-modal dialog, and therefore cannot be opened as a modal dialog.");
        }

        if (!element.IsConnected)
            throw realm.DomError("InvalidStateError", "Failed to execute 'showModal' on 'HTMLDialogElement': The element is not in a Document.");

        return _host.RunAsScriptCall(() =>
        {
            if (!_host.FireDialogEvent(element, "beforetoggle", cancelable: true, "closed", "open") || _host.HasOpenAttribute(element))
                return JsValue.Undefined;

            QueueToggleEvent(element, "closed", "open");
            _host.SetOpenAttribute(element, true);
            _host.SetDialogModal(element, true);
            _host.AssignNextTopLayerOrder(element);
            _host.InvalidateStyleScope(element);
            return JsValue.Undefined;
        });
    }

    /// <summary>
    /// <c>show()</c>: an <c>InvalidStateError</c> for a dialog open as a modal one, nothing for one already
    /// open; otherwise a cancelable <c>beforetoggle</c>, then the dialog opens and its <c>toggle</c> follows.
    /// A dialog out of a document opens too (measured).
    /// </summary>
    private JsValue Show(DomElement element, IJsRealm realm)
    {
        if (_host.HasOpenAttribute(element))
        {
            if (!_host.IsDialogModal(element))
                return JsValue.Undefined;

            throw realm.DomError("InvalidStateError",
                "Failed to execute 'show' on 'HTMLDialogElement': The dialog is already open as a modal dialog, and therefore cannot be opened as a non-modal dialog.");
        }

        return _host.RunAsScriptCall(() =>
        {
            if (!_host.FireDialogEvent(element, "beforetoggle", cancelable: true, "closed", "open") || _host.HasOpenAttribute(element))
                return JsValue.Undefined;

            QueueToggleEvent(element, "closed", "open");
            _host.SetOpenAttribute(element, true);
            _host.InvalidateStyleScope(element);
            return JsValue.Undefined;
        });
    }

    /// <summary>
    /// A dialog's close request -- <c>requestClose()</c>, or Escape on a modal dialog: a cancelable
    /// <c>cancel</c>, then, unless that is cancelled, the dialog closes (measured).
    /// </summary>
    internal void RequestClose(DomElement element, string? returnValue)
    {
        if (!_host.HasOpenAttribute(element) || !_host.FireDialogEvent(element, "cancel", cancelable: true))
            return;

        CloseDialog(element, returnValue);
    }

    private void QueueToggleEvent(DomElement element, string oldState, string newState)
    {
        if (_pendingToggles.TryGetValue(element, out var pending))
        {
            _pendingToggles[element] = (pending.OldState, newState);
            return;
        }

        _pendingToggles[element] = (oldState, newState);
        _host.QueueDialogTask(() =>
        {
            if (_pendingToggles.Remove(element, out var toggle))
                _host.FireDialogEvent(element, "toggle", cancelable: false, toggle.OldState, toggle.NewState);
        });
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

    /// <summary>
    /// Closes <paramref name="element"/> with <paramref name="returnValue"/>, as <c>close(returnValue)</c>
    /// does -- what a <c>method="dialog"</c> form's submission does to its dialog; a null result leaves the
    /// <c>returnValue</c> as it was. A closed dialog stays as it is. Otherwise <c>beforetoggle</c> fires
    /// first, <c>toggle</c> follows as a task and <c>close</c> with the next animation frame, where
    /// Chromium fires it (measured: a hidden page, which draws no frames, never hears it).
    /// </summary>
    internal void CloseDialog(DomElement element, string? returnValue)
    {
        if (!_host.HasOpenAttribute(element))
            return;

        _host.FireDialogEvent(element, "beforetoggle", cancelable: false, "open", "closed");
        if (!_host.HasOpenAttribute(element))
            return;

        QueueToggleEvent(element, "open", "closed");
        CloseNow(element, returnValue);
        _host.QueueDialogFrameAction(() => _host.FireDialogEvent(element, "close", cancelable: false));
    }

    private JsValue Close(DomElement element, in JsCall call)
    {
        // ToJsString, not the handle's rendering: `close(obj)` stores what the object's own
        // toString answers, which is the coercion a page observes on `dialog.returnValue`.
        string? returnValue = call.Length > 0 ? call.Realm.ToJsString(call[0]) : null;
        return _host.RunAsScriptCall(() =>
        {
            CloseDialog(element, returnValue);
            return JsValue.Undefined;
        });
    }

    private void CloseNow(DomElement element, string? returnValue)
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
        if (returnValue is not null)
            _host.SetReturnValue(element, returnValue);
        _host.InvalidateStyleScope(element);
    }

    private JsValue SetReturnValue(DomElement element, in JsCall call)
    {
        _host.SetReturnValue(element, call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty);
        return JsValue.Undefined;
    }
}
