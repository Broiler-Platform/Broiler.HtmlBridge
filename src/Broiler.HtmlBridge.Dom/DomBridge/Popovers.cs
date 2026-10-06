using System.Runtime.CompilerServices;
using Broiler.Dom;
using Broiler.JSeal;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// The popover API as HTML and Chromium have it: <c>showPopover()</c>, <c>hidePopover()</c> and
/// <c>togglePopover()</c> on every HTML element, the <c>popover</c> attribute's states, the auto and hint
/// popovers that close one another, an invoker's <c>popovertarget</c>, light dismiss and Escape -- with the
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
/// <b>As Chromium does it, measured</b>. A show fires a
/// cancelable <c>beforetoggle</c> first, then closes the popovers that are not its ancestors -- the topmost
/// first, each with its own <c>beforetoggle</c> -- and opens; a hide closes the popovers above it, then fires
/// a <c>beforetoggle</c> that cannot be cancelled and closes. <c>toggle</c> follows as a task, one per popover
/// for every change since its last: a second change cancels the queued task and queues a new one with the
/// first old state, so the toggle goes behind whatever was queued in between. A press outside the open
/// popovers closes them after its <c>mousedown</c>; Escape closes the topmost one after its <c>keydown</c>.
/// No popover of a document may show while another of it shows or hides: Chromium throws.
/// </para>
/// <para>
/// <b>Hint popovers are a stack of their own</b>, above the auto one (HTML's showing auto and hint popover
/// lists): showing one closes only the hint popovers it is not in, while an auto popover closes both stacks
/// -- unless it is shown in a hint popover, which makes it a hint one. The auto popover the first hint one
/// was shown in is the hint stack's parent: hiding it hides every hint popover. A press in a hint popover
/// that has no such parent closes the auto popovers.
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

    // HTML's popover visibility state "showing"; the showing auto and hint popover lists -- the popovers whose
    // "opened in popover mode" is auto or hint, in the order they went into the top layer; and for each showing
    // popover the element that invoked it and, for the first of a stack, the one that had focus before it.
    private readonly HashSet<DomElement> _showingPopovers = new(ReferenceEqualityComparer.Instance);
    private readonly List<DomElement> _autoPopovers = [];
    private readonly List<DomElement> _hintPopovers = [];
    private readonly Dictionary<DomElement, (DomElement? Invoker, DomElement? PreviouslyFocused)> _popoverShowings = new(ReferenceEqualityComparer.Instance);

    // Each document's hint stack parent: the auto popover its first hint popover was shown in.
    private readonly Dictionary<DomDocument, DomElement> _hintStackParents = new(ReferenceEqualityComparer.Instance);

    // HTML's "showing popover" of a document, and its "hiding popover nesting count": while either is set, no
    // popover of the document may show. And each element's "popover hiding": a hide asked for while its own is
    // under way finishes it at once, with no events.
    private readonly HashSet<DomDocument> _documentsShowingPopover = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<DomDocument, int> _popoverHideNesting = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<DomElement> _popoversHiding = new(ReferenceEqualityComparer.Instance);

    // The element a script gave an invoker's popoverTargetElement, which wins over its popovertarget id.
    private readonly ConditionalWeakTable<DomElement, DomElement> _explicitPopoverTargets = new();

    // The toggle event each element has queued and not fired yet (HTML's toggle task tracker), for dialogs and popovers.
    private readonly Dictionary<DomElement, ToggleTask> _pendingToggles = new(ReferenceEqualityComparer.Instance);

    private const string BeforeToggleChangedIt =
        " This might have been the result of the \"beforetoggle\" event handler changing the state of this popover.";

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

    /// <summary>Whether <paramref name="element"/> is a popover that is showing (<c>:popover-open</c>).</summary>
    internal bool IsPopoverShowing(DomElement element) => _showingPopovers.Contains(element);

    /// <summary>Whether <paramref name="element"/> is showing as an auto or a hint popover -- one that light dismiss and Escape close.</summary>
    private bool IsOpenAutoOrHintPopover(DomElement element) => _autoPopovers.Contains(element) || _hintPopovers.Contains(element);

    /// <summary>The showing hint popover list of <paramref name="document"/> when <paramref name="hint"/>, else its showing auto popover list.</summary>
    private List<DomElement> PopoverList(DomDocument document, bool hint) =>
        (hint ? _hintPopovers : _autoPopovers).FindAll(popover => ReferenceEquals(GetOwningDocument(popover), document));

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
    /// exceptions Chromium throws for an element that is no popover, is not in a document or no longer in
    /// <paramref name="expectedDocument"/>, or is open as a modal dialog. After a <c>beforetoggle</c>
    /// (<paramref name="afterEvent"/>) the message says the event may have done it, as Chromium's does.
    /// </summary>
    private bool CheckPopoverValidity(DomElement element, bool expectedToBeShowing, IJsRealm? realm, string method,
        DomDocument? expectedDocument = null, bool afterEvent = false)
    {
        if (PopoverStateOf(element) == PopoverState.None)
        {
            if (realm is null)
                return false;

            throw realm.DomError("NotSupportedError",
                $"Failed to execute '{method}' on 'HTMLElement': Not supported on elements that are not popovers." +
                (afterEvent || method == "hidePopover" ? BeforeToggleChangedIt : string.Empty));
        }

        if (IsPopoverShowing(element) != expectedToBeShowing)
            return false;

        string? problem = null;
        if (!element.IsConnected)
            problem = "Invalid on disconnected popover elements.";
        else if (expectedDocument is not null && !ReferenceEquals(GetOwningDocument(element), expectedDocument))
            problem = "Invalid when the document changes while showing or hiding a popover element.";
        else if (!expectedToBeShowing && IsModalDialog(element))
            problem = "The dialog is already open as a dialog, and therefore cannot be opened as a popover.";

        if (problem is null)
            return true;

        if (realm is null)
            return false;

        throw realm.DomError("InvalidStateError",
            $"Failed to execute '{method}' on 'HTMLElement': {problem}" + (afterEvent ? BeforeToggleChangedIt : string.Empty));
    }

    /// <summary>
    /// HTML "show popover": <c>beforetoggle</c>, the popovers it is not in closed, then it opens and its
    /// <c>toggle</c> is queued. Refused while another popover of its document shows or hides.
    /// </summary>
    private void ShowPopover(DomElement element, IJsRealm? realm, DomElement? invoker, string method = "showPopover")
    {
        var document = GetOwningDocument(element);
        if (_documentsShowingPopover.Contains(document) || _popoverHideNesting.ContainsKey(document))
        {
            if (realm is null)
                return;

            throw realm.DomError("InvalidStateError",
                $"Failed to execute '{method}' on 'HTMLElement': Invalid to show a popover during another show operation");
        }

        if (!CheckPopoverValidity(element, expectedToBeShowing: false, realm, method))
            return;

        _documentsShowingPopover.Add(document);
        try
        {
            if (!FireToggleEvent(element, "beforetoggle", cancelable: true, "closed", "open") ||
                !CheckPopoverValidity(element, expectedToBeShowing: false, realm, method, document, afterEvent: true))
            {
                return;
            }

            var shouldRestoreFocus = false;
            var originalState = PopoverStateOf(element);
            var mode = PopoverState.Manual;
            DomElement? ancestor = null;
            if (originalState is PopoverState.Auto or PopoverState.Hint)
            {
                // An auto popover shown in a hint one is a hint one: the auto stack is below the hint stack.
                mode = originalState;
                ancestor = TopmostPopoverAncestor(element, invoker);
                if (ancestor is not null && _hintPopovers.Contains(ancestor))
                    mode = PopoverState.Hint;

                HidePopoverStackUntil(document, ancestor, hint: true, focusPreviousElement: false, fireEvents: true);
                if (mode == PopoverState.Auto)
                    HidePopoverStackUntil(document, ancestor, hint: false, focusPreviousElement: false, fireEvents: true);

                if (PopoverStateOf(element) != originalState)
                {
                    if (realm is null)
                        return;

                    throw realm.DomError("InvalidStateError",
                        $"Failed to execute '{method}' on 'HTMLElement': The popover attribute changed while hiding other popovers.");
                }

                if (!CheckPopoverValidity(element, expectedToBeShowing: false, realm, method, document, afterEvent: true))
                    return;

                // Focus goes back on hide only for the first popover of a stack.
                shouldRestoreFocus = TopmostAutoOrHintPopover(document) is null;
            }

            var originallyFocused = FocusedElementIn(document);
            _showingPopovers.Add(element);
            if (mode == PopoverState.Auto)
                _autoPopovers.Add(element);
            else if (mode == PopoverState.Hint)
                _hintPopovers.Add(element);
            if (mode == PopoverState.Hint && ancestor is not null && _autoPopovers.Contains(ancestor))
                _hintStackParents[document] = ancestor;
            _popoverShowings[element] = (invoker, null);

            var state = DialogStateFor(element);
            state.PopoverOpen.Set(true);
            state.PopoverTransitioningOut.Remove();
            state.TopLayerOrder.Set(++_topLayerCounter);
            InvalidateStyleScope(element);
            NoteElementStateChange();

            // The popover focusing steps: an autofocus element in it takes focus; otherwise focus stays where it was (measured).
            if (AutofocusDelegateOf(element) is { } control)
                FocusElement(control);

            if (shouldRestoreFocus && PopoverStateOf(element) != PopoverState.None)
                _popoverShowings[element] = (invoker, originallyFocused);

            QueueToggleEvent(element, "closed", "open");
        }
        finally
        {
            _documentsShowingPopover.Remove(document);
        }
    }

    /// <summary>
    /// HTML "hide popover algorithm": the popovers above it closed first, then -- with
    /// <paramref name="fireEvents"/> -- a <c>beforetoggle</c> that cannot be cancelled, it closes and its
    /// <c>toggle</c> is queued; focus goes back to what had it before its stack showed when it is inside.
    /// </summary>
    private void HidePopover(DomElement element, bool focusPreviousElement, bool fireEvents, IJsRealm? realm, string method = "hidePopover")
    {
        if (!CheckPopoverValidity(element, expectedToBeShowing: true, realm, method))
            return;

        var document = GetOwningDocument(element);
        HidePopoverCore(element, document, focusPreviousElement, fireEvents,
            () => CheckPopoverValidity(element, expectedToBeShowing: true, realm, method, document, afterEvent: true));
    }

    /// <summary>
    /// The hide for a popover that is showing but could no longer pass the validity check: one taken out of its
    /// document, which closes with no events, or whose <c>popover</c> attribute changed its state, which closes
    /// with them (both measured).
    /// </summary>
    private void ClosePopover(DomElement element, bool focusPreviousElement, bool fireEvents)
    {
        if (IsPopoverShowing(element))
            HidePopoverCore(element, GetOwningDocument(element), focusPreviousElement, fireEvents, () => IsPopoverShowing(element));
    }

    /// <summary>
    /// The hide popover algorithm from its first step on, for <paramref name="element"/> of
    /// <paramref name="document"/>: <paramref name="stillValid"/> asks again after each step a listener can
    /// act in. A hide asked for while this one is under way finishes it with no events.
    /// </summary>
    private void HidePopoverCore(DomElement element, DomDocument document, bool focusPreviousElement, bool fireEvents, Func<bool> stillValid)
    {
        var nestedHide = !_popoversHiding.Add(element);
        if (nestedHide)
            fireEvents = false;

        _popoverHideNesting[document] = _popoverHideNesting.GetValueOrDefault(document) + 1;
        try
        {
            var inAutoList = _autoPopovers.Contains(element);
            var inHintList = _hintPopovers.Contains(element);
            if (inAutoList || inHintList)
            {
                if (inHintList)
                    HidePopoverStackUntil(document, element, hint: true, focusPreviousElement, fireEvents);

                // The hint stack's parent going takes every hint popover with it.
                if (_hintStackParents.TryGetValue(document, out var parent) && ReferenceEquals(parent, element))
                    HidePopoverStackUntil(document, endpoint: null, hint: true, focusPreviousElement, fireEvents);

                if (inAutoList)
                    HidePopoverStackUntil(document, element, hint: false, focusPreviousElement, fireEvents);

                if (!stillValid())
                    return;
            }

            if (fireEvents)
            {
                FireToggleEvent(element, "beforetoggle", cancelable: false, "open", "closed");
                if (!stillValid())
                    return;
            }

            _showingPopovers.Remove(element);
            _autoPopovers.Remove(element);
            _hintPopovers.Remove(element);
            _popoverShowings.Remove(element, out var showing);

            // CSS Position §overlay: a popover whose `overlay` is transitioned with allow-discrete stays in the
            // top layer while it animates out, which a still render catches mid-transition -- unless it is
            // removed at once, which a hide without events does.
            var state = DialogStateFor(element);
            if (fireEvents && element.IsConnected && PopoverKeepsOverlayOnHide(element))
                state.PopoverTransitioningOut.Set(true);
            else
                state.PopoverOpen.Remove();
            InvalidateStyleScope(element);
            NoteElementStateChange();

            if (_hintStackParents.TryGetValue(document, out var stackParent) &&
                (ReferenceEquals(stackParent, element) || PopoverList(document, hint: true).Count == 0))
            {
                _hintStackParents.Remove(document);
            }

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
            if (!nestedHide)
                _popoversHiding.Remove(element);

            // A listener that ended the document (ResetPopovers) has taken the count with it.
            if (_popoverHideNesting.TryGetValue(document, out var nesting))
            {
                if (nesting <= 1)
                    _popoverHideNesting.Remove(document);
                else
                    _popoverHideNesting[document] = nesting - 1;
            }
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
    /// HTML "hide popovers until": the hint popovers above <paramref name="endpoint"/>, then the auto ones above
    /// it -- or, for a hint endpoint, above the hint stack's parent; for no endpoint, every one of
    /// <paramref name="document"/>.
    /// </summary>
    private void HidePopoversUntil(DomDocument document, DomElement? endpoint, bool focusPreviousElement, bool fireEvents)
    {
        var endpointIsHint = endpoint is not null && PopoverList(document, hint: true).Contains(endpoint);
        HidePopoverStackUntil(document, endpoint, hint: true, focusPreviousElement, fireEvents);

        var autoEndpoint = endpointIsHint ? _hintStackParents.GetValueOrDefault(document) : endpoint;
        HidePopoverStackUntil(document, autoEndpoint, hint: false, focusPreviousElement, fireEvents);
    }

    /// <summary>
    /// HTML "hide popover stack until": the popovers of <paramref name="document"/>'s hint (or auto) list above
    /// <paramref name="endpoint"/> -- all of them when it is not in the list -- hidden, the topmost first; and
    /// then any that showed meanwhile, without events.
    /// </summary>
    private void HidePopoverStackUntil(DomDocument document, DomElement? endpoint, bool hint, bool focusPreviousElement, bool fireEvents)
    {
        var list = PopoverList(document, hint);
        var lastHideIndex = endpoint is null ? 0 : list.IndexOf(endpoint) + 1;
        var toRemain = list.GetRange(0, lastHideIndex);
        for (var i = list.Count - 1; i >= lastHideIndex; i--)
            HideListedPopover(list[i], focusPreviousElement, fireEvents);

        var now = PopoverList(document, hint);
        for (var i = now.Count - 1; i >= 0; i--)
        {
            if (!toRemain.Contains(now[i]))
                HideListedPopover(now[i], focusPreviousElement, fireEvents: false);
        }
    }

    /// <summary>Hides a popover of a stack; one that left its document unnoticed closes as a removed one does.</summary>
    private void HideListedPopover(DomElement popover, bool focusPreviousElement, bool fireEvents)
    {
        if (popover.IsConnected)
            HidePopover(popover, focusPreviousElement, fireEvents, realm: null);
        else
            ClosePopover(popover, focusPreviousElement: false, fireEvents: false);
    }

    /// <summary>
    /// HTML "topmost popover ancestor": of the showing auto and hint popovers -- the auto list, then the hint
    /// list -- the last that <paramref name="element"/> or <paramref name="source"/> is in; null for none.
    /// </summary>
    private DomElement? TopmostPopoverAncestor(DomElement element, DomElement? source)
    {
        var document = GetOwningDocument(element);
        var combined = PopoverList(document, hint: false);
        combined.AddRange(PopoverList(document, hint: true));

        int LastContaining(DomElement node)
        {
            for (var i = combined.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(combined[i], node) && IsInclusiveAncestor(combined[i], node))
                    return i;
            }

            return -1;
        }

        var index = Math.Max(LastContaining(element), source is null ? -1 : LastContaining(source));
        return index < 0 ? null : combined[index];
    }

    /// <summary>The nearest inclusive ancestor of <paramref name="node"/> that is showing as an auto or a hint popover.</summary>
    private DomElement? NearestInclusiveOpenPopover(DomElement node)
    {
        for (var current = node; current is not null; current = ParentEl(current))
        {
            if (IsOpenAutoOrHintPopover(current))
                return current;
        }

        return null;
    }

    /// <summary>The showing auto or hint popover an invoker at or above <paramref name="node"/> targets, the nearest first.</summary>
    private DomElement? NearestInclusiveTargetPopover(DomElement node)
    {
        for (var current = node; current is not null; current = ParentEl(current))
        {
            if (PopoverTargetElementOf(current) is { } popover && PopoverStateOf(popover) is PopoverState.Auto or PopoverState.Hint &&
                IsPopoverShowing(popover))
            {
                return popover;
            }
        }

        return null;
    }

    /// <summary>HTML "popover stack position": where <paramref name="popover"/> is in its document's auto list, then its hint list, counted from 1; 0 for neither.</summary>
    private int PopoverStackPosition(DomElement? popover)
    {
        if (popover is null)
            return 0;

        var document = GetOwningDocument(popover);
        var autoList = PopoverList(document, hint: false);
        var hintIndex = PopoverList(document, hint: true).IndexOf(popover);
        if (hintIndex >= 0)
            return hintIndex + autoList.Count + 1;

        var autoIndex = autoList.IndexOf(popover);
        return autoIndex >= 0 ? autoIndex + 1 : 0;
    }

    /// <summary>
    /// What a press at <paramref name="target"/> does to the open auto and hint popovers (HTML "light dismiss open
    /// popovers", where Chromium does it: after the press's <c>mousedown</c>): those above the topmost one it is
    /// in, or on an invoker of, close.
    /// </summary>
    private void LightDismissPopovers(DomElement target)
    {
        var document = GetOwningDocument(target);
        if (TopmostAutoOrHintPopover(document) is null)
            return;

        // HTML "topmost clicked popover": the popover the press is in, or the one its invoker targets, whichever is higher.
        var clicked = NearestInclusiveOpenPopover(target);
        var invoked = NearestInclusiveTargetPopover(target);
        var ancestor = PopoverStackPosition(clicked) > PopoverStackPosition(invoked) ? clicked : invoked;
        HidePopoversUntil(document, ancestor, focusPreviousElement: false, fireEvents: true);
    }

    /// <summary>
    /// HTML "topmost auto or hint popover" of <paramref name="document"/>: the last hint popover, else the last
    /// auto one -- what Escape closes, unless a modal dialog went into the top layer after it.
    /// </summary>
    private DomElement? TopmostAutoOrHintPopover(DomDocument document) =>
        _hintPopovers.LastOrDefault(popover => ReferenceEquals(GetOwningDocument(popover), document)) ??
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
    /// What showing a dialog does to the open popovers (HTML <c>show()</c> and <c>showModal()</c>): those above
    /// the topmost one the dialog is in close, with their events (measured: auto and hint ones, not a manual one).
    /// </summary>
    internal void HidePopoversForDialog(DomElement dialog) =>
        HidePopoversUntil(GetOwningDocument(dialog), TopmostPopoverAncestor(dialog, source: null), focusPreviousElement: false, fireEvents: true);

    /// <summary>A showing popover taken out of its document: it closes, with no events (HTML "removing steps", measured).</summary>
    private void HideRemovedPopovers(DomNode removed)
    {
        if (_showingPopovers.Count == 0)
            return;

        foreach (var popover in _showingPopovers.ToList())
        {
            if (!popover.IsConnected && (ReferenceEquals(popover, removed) || removed is DomElement root && IsInclusiveAncestor(root, popover)))
                ClosePopover(popover, focusPreviousElement: false, fireEvents: false);
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
        ClosePopover(element, focusPreviousElement: true, fireEvents: true);
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
        _hintPopovers.Clear();
        _popoverShowings.Clear();
        _hintStackParents.Clear();
        _documentsShowingPopover.Clear();
        _popoverHideNesting.Clear();
        _popoversHiding.Clear();
        foreach (var pending in _pendingToggles.Values)
            pending.Cancelled = true;
        _pendingToggles.Clear();
    }
}
