using System.Collections.Generic;
using Broiler.Dom;
using Broiler.JSeal;
using Broiler.HtmlBridge.Logging;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// A click a page's script makes -- <c>element.click()</c>, or a <c>MouseEvent</c> named <c>click</c> it
/// dispatches -- with the activation behaviour of the element it activates, and the rule every call a
/// script makes into a default action follows: its events end with no microtask checkpoint.
/// </summary>
/// <remarks>
/// <para>
/// <b>A script's click did not do what a click does.</b> <c>click()</c> toggled a checkbox and fired
/// neither <c>input</c> nor <c>change</c>, flipped it back for no cancelled click, clicked a disabled
/// control, and fired an untrusted <c>submit</c> for a submit button, unvalidated and with no
/// <c>submitter</c>; a reset button, a label and a link did nothing. A dispatched
/// <c>new MouseEvent('click')</c> ran its listeners and nothing else.
/// </para>
/// <para>
/// <b>As Chromium clicks, measured.</b> <c>click()</c> on a disabled control -- its own <c>disabled</c>
/// or a disabled fieldset's -- does nothing, nor does it on an element whose <c>click()</c> is still
/// running. Otherwise an untrusted click is dispatched, and the nearest element on its path with an
/// activation behaviour acts: a checkbox or a radio button changes before the listeners run and back if
/// they cancel the click, and otherwise gets a trusted <c>input</c> and <c>change</c> when it is in a
/// document; a label clicks its control the same way; a submit button submits its form as a user's
/// click would -- validated, with its <c>submit</c> -- and a reset button resets it; a link is followed,
/// a <c>javascript:</c> one by running its script.
/// A dispatched click that is a <c>MouseEvent</c> does the same, and one that is a plain <c>Event</c>
/// only runs its listeners.
/// </para>
/// <para>
/// <b>Script calls run no microtasks.</b> A user's input ends its task, so the microtask checkpoint
/// follows the events it fires. A script's <c>click()</c>, <c>reset()</c>, <c>requestSubmit()</c>,
/// <c>checkValidity()</c> or <c>reportValidity()</c> is a call inside a script that is still running,
/// so the promises its listeners settle run when that script ends; the three validation calls ran them
/// in the middle of it.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    // How many calls a script made into the bridge's default actions are running: while any is, the
    // events they fire end with no microtask checkpoint (DispatchKeyboardEvent, DispatchTrusted).
    private int _scriptCallDepth;

    // The elements whose click() is running: a click() on one of them does nothing (HTML "click in
    // progress flag").
    private readonly HashSet<DomElement> _clicksInProgress = new(ReferenceEqualityComparer.Instance);

    /// <summary>Runs <paramref name="call"/>, a script's call into a default action: the events it fires end with no microtask checkpoint.</summary>
    private T RunAsScriptCall<T>(Func<T> call)
    {
        _scriptCallDepth++;
        try
        {
            return call();
        }
        finally
        {
            _scriptCallDepth--;
        }
    }

    /// <inheritdoc cref="RunAsScriptCall{T}(Func{T})"/>
    private void RunAsScriptCall(Action call) => RunAsScriptCall(() =>
    {
        call();
        return true;
    });

    /// <summary>Whether the microtask checkpoint follows an event the bridge fires now: no script call into a default action is running.</summary>
    private bool EndsTaskWithCheckpoint => _scriptCallDepth == 0;

    /// <summary>
    /// <c>element.click()</c>: an untrusted click at <paramref name="element"/>, with the activation
    /// behaviour of the element it activates. A disabled form control is not clicked, nor is an element
    /// whose <c>click()</c> is still running.
    /// </summary>
    private void ClickByScript(DomElement element)
    {
        if (IsDisabledFormControl(element) || !_clicksInProgress.Add(element))
            return;

        try
        {
            RunAsScriptCall(() => DispatchScriptClick(element, NewScriptClickEvent(element)));
        }
        finally
        {
            _clicksInProgress.Remove(element);
        }
    }

    /// <summary>
    /// <c>dispatchEvent(evt)</c> at <paramref name="node"/>: the event's listeners, and for a
    /// <c>MouseEvent</c> named <c>click</c> at an element, the activation behaviour of the element it
    /// activates. Answers whether it was not cancelled.
    /// </summary>
    private bool DispatchEventByScript(DomNode node, JsValue evt) =>
        node is DomElement element && IsActivatingClick(evt)
            ? RunAsScriptCall(() => DispatchScriptClick(element, evt))
            : _eventDispatch.DispatchEventOnElement(node, evt).AsBoolean;

    /// <summary>
    /// Dispatches <paramref name="evt"/>, a click a script fired, at <paramref name="target"/>, with the
    /// activation behaviour of the element it activates (DOM "dispatch", steps 5.4, 5.9, 11 and 14).
    /// Answers whether it was not cancelled.
    /// </summary>
    private bool DispatchScriptClick(DomElement target, JsValue evt)
    {
        var activation = ActivationElementOf(target);
        var undo = PreActivate(activation, out var changed);

        if (!DispatchKeyboardEvent(target, evt))
        {
            undo?.Invoke();
            return false;
        }

        if (activation is not null)
            ActivateByScript(activation, target, changed);
        return true;
    }

    /// <summary>
    /// The element a click at <paramref name="target"/> activates: the target or its nearest ancestor
    /// with an activation behaviour -- an input, a button, a label or a link -- or null.
    /// </summary>
    /// <remarks>
    /// An input or a button that does nothing when clicked still stops the search, as it does a user's
    /// click (<see cref="ActivationTargetOf"/>): a click on a text field inside a label does not click the
    /// label's control.
    /// </remarks>
    private static DomElement? ActivationElementOf(DomElement target)
    {
        for (var current = target; current != null; current = ParentEl(current))
        {
            if (current.TagName.ToLowerInvariant() is "input" or "button" or "label" || IsHyperlink(current))
                return current;
        }

        return null;
    }

    /// <summary>
    /// The activation behaviour of <paramref name="activation"/> for a script's click at
    /// <paramref name="target"/> that nobody cancelled; <paramref name="changed"/> says whether a checkbox
    /// or a radio button it is changed before the click.
    /// </summary>
    private void ActivateByScript(DomElement activation, DomElement target, bool changed)
    {
        if (IsCheckable(activation, out _))
        {
            // Not the user's change, so not :user-valid or :user-invalid (DomBridge/ElementStates.cs).
            if (changed && activation.IsConnected)
            {
                FireChangeNotification(activation, "input", composed: true);
                FireChangeNotification(activation, "change", composed: false);
            }

            return;
        }

        if (activation.TagName.Equals("label", StringComparison.OrdinalIgnoreCase))
        {
            // A label clicks the control it labels, which then does what its own click does.
            if (LabeledControlOf(activation) is { } control && !IsInclusiveAncestor(control, target))
                ClickByScript(control);
            return;
        }

        if (IsDisabledFormControl(activation))
            return;

        if (IsSubmitButton(activation))
        {
            if (FormOwnerOf(activation) is { } form)
                SubmitForm(form, activation);
        }
        else if (IsResetButton(activation))
        {
            if (FormOwnerOf(activation) is { } form)
                ResetForm(form);
        }
        else if (IsHyperlink(activation))
        {
            FollowHyperlinkByScript(activation);
        }
    }

    /// <summary>A trusted <c>input</c> or <c>change</c> at a control a script's click changed: not cancelable, and only <c>input</c> leaves a shadow tree.</summary>
    private void FireChangeNotification(DomElement control, string type, bool composed)
    {
        var realm = Realm;
        DispatchKeyboardEvent(control, NewTrustedEvent(realm, type, bubbles: true, cancelable: false, composed, InterfacePrototype(realm, "Event")));
    }

    /// <summary>
    /// Follows a link a script's click activated, as a user's would be -- unless following it would take
    /// the page somewhere this window cannot show: another window (<c>target="_blank"</c> or a name), or
    /// a download. A <c>javascript:</c> link runs its script (DomBridge/JavaScriptUrl.cs).
    /// </summary>
    /// <remarks>
    /// A script's click opens no window: Chromium blocks a pop-up a script opens without the user's
    /// activation, and this window has no other window to open one in. A download link would otherwise
    /// replace the page with the file it names.
    /// </remarks>
    private void FollowHyperlinkByScript(DomElement link)
    {
        var target = TryGetAttribute(link, "target", out var declared) ? declared.Trim().ToLowerInvariant() : string.Empty;
        if (target is not ("" or "_self" or "_top" or "_parent"))
        {
            RenderLogger.LogDebug(LogCategory.JavaScript, "DomBridge.click",
                $"A script's click on a link to another window (target=\"{declared}\") opens nothing");
            return;
        }

        if (HasAttr(link, "download"))
        {
            RenderLogger.LogDebug(LogCategory.JavaScript, "DomBridge.click",
                "A script's click on a download link downloads nothing, and does not navigate to the file");
            return;
        }

        FollowHyperlink(link);
    }

    /// <summary>
    /// Whether <paramref name="evt"/> is a click a dispatch activates an element with: a <c>MouseEvent</c>
    /// -- an instance of the window's <c>MouseEvent</c> -- named <c>click</c> (DOM "dispatch",
    /// <i>isActivationEvent</i>).
    /// </summary>
    private bool IsActivatingClick(JsValue evt)
    {
        var realm = Realm;
        var type = realm.GetProperty(evt, "type");
        if (!type.IsString || type.AsString != "click")
            return false;

        var mouseEvent = MouseEventPrototype(realm);
        if (!mouseEvent.IsObject)
            return false;

        // A prototype chain is finite, but a proxy's need not answer the same way twice; a bound keeps
        // the walk from following one round in circles.
        var prototype = realm.GetPrototype(evt);
        for (var depth = 0; depth < 64 && prototype.IsObject; depth++)
        {
            if (prototype == mouseEvent)
                return true;
            prototype = realm.GetPrototype(prototype);
        }

        return false;
    }

    /// <summary>
    /// The click <c>click()</c> fires, as Chromium makes it (measured): a pointer event with no pointer --
    /// <c>pointerId</c> -1 and an empty <c>pointerType</c> -- untrusted, bubbling, cancelable and composed,
    /// its coordinates, buttons and modifier keys all zero. A <c>PointerEvent</c>, and so a
    /// <c>MouseEvent</c>, to <c>instanceof</c>.
    /// </summary>
    private JsValue NewScriptClickEvent(DomElement target)
    {
        var realm = Realm;
        var evt = NewEvent(realm, "click", bubbles: true, cancelable: true, composed: true, PointerEventPrototype(realm), trusted: false);
        var window = WindowOfDocument(GetOwningDocument(target));
        Define(realm, evt, "view", window.IsObject ? window : JsValue.Null);
        Define(realm, evt, "detail", JsValue.Number(0));
        foreach (var zero in ScriptClickZeroes)
            Define(realm, evt, zero, JsValue.Number(0));
        foreach (var modifier in ScriptClickModifiers)
            Define(realm, evt, modifier, JsValue.False);
        Define(realm, evt, "relatedTarget", JsValue.Null);
        realm.DefineMethod(evt, "getModifierState", 1, static (in _) => JsValue.False);

        Define(realm, evt, "pointerId", JsValue.Number(-1));
        Define(realm, evt, "pointerType", JsValue.String(string.Empty));
        Define(realm, evt, "isPrimary", JsValue.False);
        Define(realm, evt, "width", JsValue.Number(1));
        Define(realm, evt, "height", JsValue.Number(1));
        Define(realm, evt, "pressure", JsValue.Number(0));
        Define(realm, evt, "tangentialPressure", JsValue.Number(0));
        Define(realm, evt, "tiltX", JsValue.Number(0));
        Define(realm, evt, "tiltY", JsValue.Number(0));
        Define(realm, evt, "twist", JsValue.Number(0));
        Define(realm, evt, "altitudeAngle", JsValue.Number(Math.PI / 2));
        Define(realm, evt, "azimuthAngle", JsValue.Number(0));
        return evt;
    }

    private static readonly string[] ScriptClickZeroes =
        ["screenX", "screenY", "clientX", "clientY", "x", "y", "pageX", "pageY", "offsetX", "offsetY", "movementX", "movementY", "button", "buttons"];

    private static readonly string[] ScriptClickModifiers = ["ctrlKey", "shiftKey", "altKey", "metaKey"];

    private void ResetScriptActivation() => _clicksInProgress.Clear();
}
