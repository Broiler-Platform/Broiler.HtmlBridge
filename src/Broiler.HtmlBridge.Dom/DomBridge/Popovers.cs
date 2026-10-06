using System.Runtime.CompilerServices;
using Broiler.Dom;
using Broiler.JSeal;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// The popover API as HTML and Chromium have it: <c>showPopover()</c>, <c>hidePopover()</c> and
/// <c>togglePopover()</c> on every HTML element, the <c>popover</c> attribute's states, the auto popovers
/// that close one another, an invoker's <c>popovertarget</c>, light dismiss and Escape -- with the
/// <c>beforetoggle</c> and <c>toggle</c> events of each change.
/// </summary>
/// <remarks>
/// <para>
/// <b>A popover changed and nobody heard.</b> <c>showPopover()</c> and <c>hidePopover()</c> were own members
/// of an element that had the attribute when its wrapper was made, they fired nothing, checked nothing and
/// knew no other popover; there was no <c>togglePopover()</c>, no <c>popover</c> property, no invoker, no
/// light dismiss, and a closed popover was drawn in the flow, since no rule hid it.
/// </para>
/// <para>
/// <b>As Chromium does it, measured</b>. A show fires a cancelable
/// <c>beforetoggle</c> first, then closes the auto popovers that are not its ancestors -- the innermost
/// first, each with its own <c>beforetoggle</c> -- and opens; a hide closes the auto popovers above it, then
/// fires a <c>beforetoggle</c> that cannot be cancelled and closes. <c>toggle</c> follows as a task, one per
/// popover for every change since its last: a second change cancels the queued task and queues a new one
/// with the first old state, so the toggle goes behind whatever was queued in between. A press outside the
/// open auto popovers closes them after its <c>mousedown</c>; Escape closes the topmost one after its
/// <c>keydown</c>.
/// </para>
/// <para>
/// A <c>hint</c> popover is an auto one here: Chromium keeps a stack of its own for them, which no page
/// this bridge has met needs.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    private enum PopoverState
    {
        None,
        Auto,
        Manual,
        Hint,
    }

    // HTML's popover visibility state "showing", the auto popovers among them in the order they showed, and
    // for each the element that invoked it and the one that had focus before it showed.
    private readonly HashSet<DomElement> _showingPopovers = new(ReferenceEqualityComparer.Instance);
    private readonly List<DomElement> _autoPopovers = [];
    private readonly Dictionary<DomElement, (DomElement? Invoker, DomElement? PreviouslyFocused)> _popoverShowings = new(ReferenceEqualityComparer.Instance);

    // The popovers whose beforetoggle is being fired: a show or a hide a listener asks for meanwhile does nothing.
    private readonly HashSet<DomElement> _popoversChanging = new(ReferenceEqualityComparer.Instance);

    // The element a script gave an invoker's popoverTargetElement, which wins over its popovertarget id.
    private readonly ConditionalWeakTable<DomElement, DomElement> _explicitPopoverTargets = new();

    // The toggle event each element has queued and not fired yet (HTML's toggle task tracker), for dialogs and popovers.
    private readonly Dictionary<DomElement, ToggleTask> _pendingToggles = new(ReferenceEqualityComparer.Instance);

    private sealed class ToggleTask(string oldState, string newState)
    {
        public string OldState { get; } = oldState;

        public string NewState { get; } = newState;

        public bool Cancelled { get; set; }
    }

    private static PopoverState PopoverStateOf(DomElement element) =>
        !TryGetAttribute(element, "popover", out var value)
            ? PopoverState.None
            : value.Trim().ToLowerInvariant() switch
            {
                "" or "auto" => PopoverState.Auto,
                "hint" => PopoverState.Hint,
                _ => PopoverState.Manual,
            };

    private static bool IsAutoPopover(DomElement element) => PopoverStateOf(element) is PopoverState.Auto or PopoverState.Hint;

    /// <summary>Whether <paramref name="element"/> is a popover that is showing (<c>:popover-open</c>).</summary>
    internal bool IsPopoverShowing(DomElement element) => _showingPopovers.Contains(element);

    /// <summary>
    /// Queues <paramref name="element"/>'s <c>toggle</c> (HTML "queue a popover toggle event task", and the
    /// dialog's): one queued already is cancelled and its old state kept, and the new one goes to the back of
    /// the queue (measured: behind a timer set between the two changes).
    /// </summary>
    internal void QueueToggleEvent(DomElement element, string oldState, string newState)
    {
        if (_pendingToggles.Remove(element, out var pending))
        {
            pending.Cancelled = true;
            oldState = pending.OldState;
        }

        var task = new ToggleTask(oldState, newState);
        _pendingToggles[element] = task;
        _eventLoop.QueueTask(() =>
        {
            if (task.Cancelled || _realm is null)
                return;

            _pendingToggles.Remove(element);
            FireToggleEvent(element, "toggle", cancelable: false, task.OldState, task.NewState);
        });
    }

    /// <summary>
    /// Fires the trusted, non-bubbling <paramref name="type"/> at <paramref name="element"/> -- a
    /// <c>ToggleEvent</c> carrying <paramref name="oldState"/> and <paramref name="newState"/> when they are
    /// given -- and answers whether it was not cancelled.
    /// </summary>
    private bool FireToggleEvent(DomElement element, string type, bool cancelable, string? oldState = null, string? newState = null)
    {
        var realm = Realm;
        var evt = NewTrustedEvent(realm, type, bubbles: false, cancelable, composed: false,
            InterfacePrototype(realm, oldState is null ? "Event" : "ToggleEvent"));
        if (oldState is not null)
        {
            Define(realm, evt, "oldState", JsValue.String(oldState));
            Define(realm, evt, "newState", JsValue.String(newState ?? string.Empty));
        }

        return DispatchKeyboardEvent(element, evt);
    }

    // ── The members ──────────────────────────────────────────────────────────

    /// <summary>
    /// <c>popover</c>, <c>showPopover()</c>, <c>hidePopover()</c> and <c>togglePopover()</c>, which every HTML
    /// element has (measured: on <c>HTMLElement.prototype</c>), and <c>inert</c>.
    /// </summary>
    private void InstallPopoverMembers(JsValue target, Dom.Features.JsElementSource element)
    {
        // popover reflects its attribute's state: absent null, empty or "auto" -> "auto", "hint", anything else
        // -> "manual" (measured); null removes it.
        Realm.DefineAccessor(target, "popover",
            (in call) => PopoverStateOf(element(in call, "popover")) switch
            {
                PopoverState.None => JsValue.Null,
                PopoverState.Auto => JsValue.String("auto"),
                PopoverState.Hint => JsValue.String("hint"),
                _ => JsValue.String("manual"),
            },
            (in call) =>
            {
                var popover = element(in call, "popover");
                if (call.Length == 0 || call[0].IsNull)
                    RemoveAttr(popover, "popover");
                else
                    SetAttr(popover, "popover", call.Realm.ToJsString(call[0]));
                return JsValue.Undefined;
            });

        Realm.DefineMethod(target, "showPopover", 0, (in call) =>
        {
            var popover = element(in call, "showPopover");
            var realm = call.Realm;
            return RunAsScriptCall(() =>
            {
                ShowPopover(popover, realm, invoker: null);
                return JsValue.Undefined;
            });
        });

        Realm.DefineMethod(target, "hidePopover", 0, (in call) =>
        {
            var popover = element(in call, "hidePopover");
            var realm = call.Realm;
            return RunAsScriptCall(() =>
            {
                HidePopover(popover, focusPreviousElement: true, fireEvents: true, realm);
                return JsValue.Undefined;
            });
        });

        Realm.DefineMethod(target, "togglePopover", 0, (in call) =>
        {
            var popover = element(in call, "togglePopover");
            var realm = call.Realm;
            bool? force = null;
            if (call.Length > 0 && !call[0].IsUndefined)
            {
                var options = call[0];
                if (options.IsObject)
                {
                    var forced = realm.GetProperty(options, "force");
                    if (!forced.IsUndefined)
                        force = realm.ToBoolean(forced);
                }
                else
                {
                    force = realm.ToBoolean(options);
                }
            }

            return RunAsScriptCall(() => JsValue.Boolean(TogglePopover(popover, force, realm)));
        });

        // inert reflects its attribute; an inert element and what is in it cannot be focused or hit (DomBridge/Inertness.cs).
        Realm.DefineAccessor(target, "inert",
            (in call) => JsValue.Boolean(HasAttr(element(in call, "inert"), "inert")),
            (in call) =>
            {
                var inertElement = element(in call, "inert");
                if (call.Length > 0 && call.Realm.ToBoolean(call[0]))
                    SetAttr(inertElement, "inert", string.Empty);
                else
                    RemoveAttr(inertElement, "inert");
                return JsValue.Undefined;
            });
    }

    /// <summary><c>popoverTargetElement</c> and <c>popoverTargetAction</c> of a <c>button</c> or an <c>input</c>.</summary>
    private void InstallPopoverInvokerMembers(JsValue target, DomElement invoker)
    {
        Realm.DefineAccessor(target, "popoverTargetElement",
            (in _) => PopoverTargetElementOf(invoker) is { } popover ? WrapNode(popover) : JsValue.Null,
            (in call) =>
            {
                var value = call.Length > 0 ? call[0] : JsValue.Null;
                if (value.IsNull || value.IsUndefined)
                {
                    _explicitPopoverTargets.Remove(invoker);
                    RemoveAttr(invoker, "popovertarget");
                    return JsValue.Undefined;
                }

                if (FindDomElementByJSObject(value) is not { } popover)
                    throw call.Realm.Error(JsErrorKind.TypeError,
                        "Failed to set the 'popoverTargetElement' property on 'HTMLButtonElement': Failed to convert value to 'Element'.");

                _explicitPopoverTargets.AddOrUpdate(invoker, popover);
                SetAttr(invoker, "popovertarget", string.Empty);
                return JsValue.Undefined;
            });

        Realm.DefineAccessor(target, "popoverTargetAction",
            (in _) => JsValue.String(PopoverTargetActionOf(invoker)),
            (in call) =>
            {
                SetAttr(invoker, "popovertargetaction", call.Length > 0 ? call.Realm.ToJsString(call[0]) : "undefined");
                return JsValue.Undefined;
            });
    }

    private static string PopoverTargetActionOf(DomElement invoker) =>
        TryGetAttribute(invoker, "popovertargetaction", out var action) && action.Trim().ToLowerInvariant() is "show" or "hide"
            ? action.Trim().ToLowerInvariant()
            : "toggle";

    /// <summary>
    /// HTML "popover target element" of <paramref name="node"/>: the popover a button, or an input that is a
    /// button, names by its <c>popovertarget</c> -- unless it is disabled, or a submit button of a form, which
    /// submits the form instead.
    /// </summary>
    private DomElement? PopoverTargetElementOf(DomElement node)
    {
        var tag = node.TagName.ToLowerInvariant();
        var isButton = tag == "button" ||
                       tag == "input" && (TryGetAttribute(node, "type", out var type) ? type.Trim().ToLowerInvariant() : "text")
                           is "button" or "submit" or "reset" or "image";
        if (!isButton || IsDisabledFormControl(node) || IsSubmitButton(node) && FormOwnerOf(node) is not null)
            return null;

        DomElement? popover;
        if (_explicitPopoverTargets.TryGetValue(node, out var explicitTarget))
        {
            popover = explicitTarget.IsConnected && ReferenceEquals(GetOwningDocument(explicitTarget), GetOwningDocument(node)) ? explicitTarget : null;
        }
        else
        {
            if (!TryGetAttribute(node, "popovertarget", out var id) || id.Length == 0)
                return null;

            popover = GetOwningDocument(node).Descendants().OfType<DomElement>()
                .FirstOrDefault(element => TryGetAttribute(element, "id", out var elementId) && elementId == id);
        }

        return popover is not null && PopoverStateOf(popover) != PopoverState.None ? popover : null;
    }

    // ── The algorithms ───────────────────────────────────────────────────────

    /// <summary>
    /// HTML "check popover validity": whether <paramref name="element"/> may go to the state it is not in --
    /// false, silently, when it is already there; for a script's call (<paramref name="realm"/> given) the
    /// exceptions Chromium throws for an element that is no popover, is not in a document, or is open as a
    /// modal dialog.
    /// </summary>
    private bool CheckPopoverValidity(DomElement element, bool expectedToBeShowing, IJsRealm? realm, string method)
    {
        if (PopoverStateOf(element) == PopoverState.None)
        {
            if (realm is null)
                return false;

            throw realm.DomError("NotSupportedError",
                $"Failed to execute '{method}' on 'HTMLElement': Not supported on elements that are not popovers." +
                (method == "hidePopover" ? " This might have been the result of the \"beforetoggle\" event handler changing the state of this popover." : string.Empty));
        }

        if (IsPopoverShowing(element) != expectedToBeShowing)
            return false;

        if (!element.IsConnected)
        {
            if (realm is null)
                return false;

            throw realm.DomError("InvalidStateError", $"Failed to execute '{method}' on 'HTMLElement': Invalid on disconnected popover elements.");
        }

        if (IsModalDialog(element))
        {
            if (realm is null)
                return false;

            throw realm.DomError("InvalidStateError",
                $"Failed to execute '{method}' on 'HTMLElement': The dialog is already open as a dialog, and therefore cannot be opened as a popover.");
        }

        return true;
    }

    /// <summary>HTML "show popover": <c>beforetoggle</c>, the other auto popovers closed, then it opens and its <c>toggle</c> is queued.</summary>
    private void ShowPopover(DomElement element, IJsRealm? realm, DomElement? invoker, string method = "showPopover")
    {
        if (!CheckPopoverValidity(element, expectedToBeShowing: false, realm, method) || !_popoversChanging.Add(element))
            return;

        try
        {
            if (!FireToggleEvent(element, "beforetoggle", cancelable: true, "closed", "open") ||
                !CheckPopoverValidity(element, expectedToBeShowing: false, realm, method))
            {
                return;
            }

            var document = GetOwningDocument(element);
            var originalState = PopoverStateOf(element);
            if (IsAutoPopover(element))
            {
                HideAllPopoversUntil(TopmostPopoverAncestor(element, invoker), document, focusPreviousElement: false, fireEvents: true);
                if (PopoverStateOf(element) != originalState || !CheckPopoverValidity(element, expectedToBeShowing: false, realm, method))
                    return;
            }

            var previouslyFocused = FocusedElementIn(document);
            _showingPopovers.Add(element);
            if (IsAutoPopover(element))
                _autoPopovers.Add(element);
            _popoverShowings[element] = (invoker, previouslyFocused);

            var state = DialogStateFor(element);
            state.PopoverOpen.Set(true);
            state.PopoverTransitioningOut.Remove();
            state.TopLayerOrder.Set(++_topLayerCounter);
            InvalidateStyleScope(element);
            NoteElementStateChange();

            // The popover focusing steps: an autofocus element in it takes focus; otherwise focus stays where it was (measured).
            if (AutofocusDelegateOf(element) is { } control)
                FocusElement(control);

            QueueToggleEvent(element, "closed", "open");
        }
        finally
        {
            _popoversChanging.Remove(element);
        }
    }

    /// <summary>
    /// HTML "hide popover algorithm": the auto popovers above it closed first, then -- with
    /// <paramref name="fireEvents"/> -- a <c>beforetoggle</c> that cannot be cancelled, it closes and its
    /// <c>toggle</c> is queued; focus goes back to what had it before it showed when it is inside.
    /// </summary>
    private void HidePopover(DomElement element, bool focusPreviousElement, bool fireEvents, IJsRealm? realm, string method = "hidePopover")
    {
        if (!CheckPopoverValidity(element, expectedToBeShowing: true, realm, method))
            return;

        ClosePopover(element, focusPreviousElement, fireEvents,
            () => CheckPopoverValidity(element, expectedToBeShowing: true, realm, method));
    }

    /// <summary>
    /// The hide itself, for a popover that is showing: what <see cref="HidePopover"/> does once the popover is
    /// found valid, and what a popover taken out of its document or given another <c>popover</c> state
    /// undergoes without that check, which it could no longer pass. <paramref name="stillValid"/> asks again
    /// after each event a listener can act in.
    /// </summary>
    private void ClosePopover(DomElement element, bool focusPreviousElement, bool fireEvents, Func<bool> stillValid)
    {
        if (!IsPopoverShowing(element) || !_popoversChanging.Add(element))
            return;

        try
        {
            var document = GetOwningDocument(element);
            if (_autoPopovers.Contains(element))
            {
                HideAllPopoversUntil(element, document, focusPreviousElement, fireEvents);
                if (!stillValid())
                    return;
            }

            if (fireEvents)
            {
                FireToggleEvent(element, "beforetoggle", cancelable: false, "open", "closed");
                if (_autoPopovers.Contains(element) && !ReferenceEquals(_autoPopovers[^1], element))
                    HideAllPopoversUntil(element, document, focusPreviousElement, fireEvents: false);
                if (!stillValid())
                    return;
            }

            _showingPopovers.Remove(element);
            _autoPopovers.Remove(element);
            _popoverShowings.Remove(element, out var showing);

            // CSS Position §overlay: a popover whose `overlay` is transitioned with allow-discrete stays in the
            // top layer while it animates out, which a still render catches mid-transition.
            var state = DialogStateFor(element);
            if (element.IsConnected && PopoverKeepsOverlayOnHide(element))
                state.PopoverTransitioningOut.Set(true);
            else
                state.PopoverOpen.Remove();
            InvalidateStyleScope(element);
            NoteElementStateChange();

            if (fireEvents)
                QueueToggleEvent(element, "open", "closed");

            if (focusPreviousElement && showing.PreviouslyFocused is { IsConnected: true } previous &&
                FocusedElementIn(document) is { } focused && IsInclusiveAncestor(element, focused))
            {
                FocusElement(previous);
            }
        }
        finally
        {
            _popoversChanging.Remove(element);
        }
    }

    /// <summary><c>togglePopover(force)</c>: hides a showing popover, shows a hidden one, as <paramref name="force"/> allows; answers whether it is showing.</summary>
    private bool TogglePopover(DomElement element, bool? force, IJsRealm realm)
    {
        if (IsPopoverShowing(element) && force is null or false)
            HidePopover(element, focusPreviousElement: true, fireEvents: true, realm, "togglePopover");
        else if (!IsPopoverShowing(element) && force is null or true)
            ShowPopover(element, realm, invoker: null, "togglePopover");
        else
            CheckPopoverValidity(element, expectedToBeShowing: IsPopoverShowing(element), realm, "togglePopover");

        return IsPopoverShowing(element);
    }

    /// <summary>
    /// HTML "hide all popovers until": every auto popover of <paramref name="document"/> above
    /// <paramref name="endpoint"/> -- all of them for none -- closed, the topmost first.
    /// </summary>
    private void HideAllPopoversUntil(DomElement? endpoint, DomDocument document, bool focusPreviousElement, bool fireEvents)
    {
        if (endpoint is not null && !IsPopoverShowing(endpoint))
            return;

        // A listener can show another popover while these close, so the stack is read again each time; one
        // that does not close -- it is changing already -- ends the walk rather than holding it.
        for (var guard = 0; guard < 256; guard++)
        {
            var stack = _autoPopovers.Where(popover => ReferenceEquals(GetOwningDocument(popover), document)).ToList();
            var kept = endpoint is null ? 0 : stack.IndexOf(endpoint) + 1;
            if (kept >= stack.Count)
                return;

            var topmost = stack[^1];
            if (topmost.IsConnected)
                HidePopover(topmost, focusPreviousElement, fireEvents, realm: null);
            else
                ClosePopover(topmost, focusPreviousElement: false, fireEvents: false, () => IsPopoverShowing(topmost));
            if (_autoPopovers.Contains(topmost))
                return;
        }
    }

    /// <summary>
    /// HTML "topmost popover ancestor": of the open auto popovers <paramref name="popover"/> is in, through its
    /// parent, or <paramref name="invoker"/> is in, the one highest in the stack; null for none.
    /// </summary>
    private DomElement? TopmostPopoverAncestor(DomElement popover, DomElement? invoker)
    {
        DomElement? topmost = null;
        void Check(DomElement? candidate)
        {
            if (candidate is null || NearestInclusiveOpenPopover(candidate) is not { } ancestor ||
                ReferenceEquals(ancestor, popover))
            {
                return;
            }

            if (topmost is null || _autoPopovers.IndexOf(topmost) < _autoPopovers.IndexOf(ancestor))
                topmost = ancestor;
        }

        Check(ParentEl(popover));
        Check(invoker);
        return topmost;
    }

    /// <summary>The nearest inclusive ancestor of <paramref name="node"/> that is an open auto popover.</summary>
    private DomElement? NearestInclusiveOpenPopover(DomElement node)
    {
        for (var current = node; current is not null; current = ParentEl(current))
        {
            if (_autoPopovers.Contains(current))
                return current;
        }

        return null;
    }

    /// <summary>The open auto popover an invoker at or above <paramref name="node"/> targets, the nearest first.</summary>
    private DomElement? NearestInclusiveTargetPopoverForInvoker(DomElement node)
    {
        for (var current = node; current is not null; current = ParentEl(current))
        {
            if (PopoverTargetElementOf(current) is { } popover && _autoPopovers.Contains(popover))
                return popover;
        }

        return null;
    }

    /// <summary>
    /// What a press at <paramref name="target"/> does to the open auto popovers (HTML "light dismiss open
    /// popovers", where Chromium does it: after the press's <c>mousedown</c>): those the press is not in, or
    /// not on an invoker of, close.
    /// </summary>
    private void LightDismissPopovers(DomElement target)
    {
        var document = GetOwningDocument(target);
        if (!_autoPopovers.Any(popover => ReferenceEquals(GetOwningDocument(popover), document)))
            return;

        var clicked = NearestInclusiveOpenPopover(target);
        var invoked = NearestInclusiveTargetPopoverForInvoker(target);
        var ancestor = clicked is null ? invoked
            : invoked is null ? clicked
            : _autoPopovers.IndexOf(clicked) > _autoPopovers.IndexOf(invoked) ? clicked : invoked;
        HideAllPopoversUntil(ancestor, document, focusPreviousElement: false, fireEvents: true);
    }

    /// <summary>The topmost open auto popover of <paramref name="document"/>, which Escape closes, and its place in the top layer.</summary>
    private DomElement? TopmostAutoPopover(DomDocument document) =>
        _autoPopovers.LastOrDefault(popover => ReferenceEquals(GetOwningDocument(popover), document));

    /// <summary>
    /// HTML "popover target attribute activation behavior": a click on <paramref name="invoker"/> shows or hides
    /// the popover it targets, as its <c>popovertargetaction</c> says -- unless the click was in the popover,
    /// which is in the invoker.
    /// </summary>
    private void ActivatePopoverTarget(DomElement invoker, DomElement eventTarget)
    {
        if (PopoverTargetElementOf(invoker) is not { } popover)
            return;

        if (IsInclusiveAncestor(popover, eventTarget) && IsInclusiveAncestor(invoker, popover) && !ReferenceEquals(invoker, popover))
            return;

        var action = PopoverTargetActionOf(invoker);
        if (action == "show" && IsPopoverShowing(popover) || action == "hide" && !IsPopoverShowing(popover))
            return;

        if (IsPopoverShowing(popover))
            HidePopover(popover, focusPreviousElement: true, fireEvents: true, realm: null);
        else if (CheckPopoverValidity(popover, expectedToBeShowing: false, realm: null, "showPopover"))
            ShowPopover(popover, realm: null, invoker);
    }

    /// <summary>
    /// What showing a modal dialog does to the open popovers (HTML <c>showModal()</c>): the auto ones the
    /// dialog is not in close, with their events (measured; a manual one stays).
    /// </summary>
    internal void HidePopoversForModalDialog(DomElement dialog)
    {
        var document = GetOwningDocument(dialog);
        HideAllPopoversUntil(NearestInclusiveOpenPopover(dialog), document, focusPreviousElement: false, fireEvents: true);
    }

    /// <summary>A showing popover taken out of its document: it closes, with no events (HTML "removing steps", measured).</summary>
    private void HideRemovedPopovers(DomNode removed)
    {
        if (_showingPopovers.Count == 0)
            return;

        foreach (var popover in _showingPopovers.ToList())
        {
            if (!popover.IsConnected && (ReferenceEquals(popover, removed) || removed is DomElement root && IsInclusiveAncestor(root, popover)))
                ClosePopover(popover, focusPreviousElement: false, fireEvents: false, () => IsPopoverShowing(popover));
        }
    }

    /// <summary>A showing popover whose <c>popover</c> attribute changed its state: it closes, with its events (HTML's attribute change steps, measured).</summary>
    private void OnPopoverAttributeChanged(DomElement element, string? oldValue)
    {
        if (!IsPopoverShowing(element))
            return;

        var oldState = oldValue is null
            ? PopoverState.None
            : oldValue.Trim().ToLowerInvariant() switch
            {
                "" or "auto" => PopoverState.Auto,
                "hint" => PopoverState.Hint,
                _ => PopoverState.Manual,
            };
        if (oldState == PopoverStateOf(element))
            return;

        // Hidden as the popover it was shown as: the validity check asks for one, which a removed attribute no longer makes it.
        ClosePopover(element, focusPreviousElement: true, fireEvents: true, () => IsPopoverShowing(element));
    }

    /// <summary>
    /// Whether <paramref name="element"/> is a popover that generates no box: not showing, not still in the top
    /// layer for an <c>overlay</c> transition, and not an open dialog.
    /// </summary>
    private bool IsClosedPopover(DomElement element) =>
        PopoverStateOf(element) != PopoverState.None &&
        !(_dialogRuntimeStates.TryGetValue(element, out var state) && state.PopoverOpen is { IsSet: true, Value: true }) &&
        !(element.TagName.Equals("dialog", StringComparison.OrdinalIgnoreCase) && HasAttr(element, "open"));

    /// <summary>
    /// HTML's "autofocus delegate": the first element in <paramref name="element"/> with an <c>autofocus</c>
    /// attribute that can be focused, or null.
    /// </summary>
    private DomElement? AutofocusDelegateOf(DomElement element) =>
        element.Descendants().OfType<DomElement>()
            .FirstOrDefault(candidate => HasAttr(candidate, "autofocus") && IsFocusable(candidate));

    /// <summary>Forgets every popover and toggle of the document that is going away.</summary>
    private void ResetPopovers()
    {
        _showingPopovers.Clear();
        _autoPopovers.Clear();
        _popoverShowings.Clear();
        _popoversChanging.Clear();
        foreach (var pending in _pendingToggles.Values)
            pending.Cancelled = true;
        _pendingToggles.Clear();
    }
}
