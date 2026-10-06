using System.Collections.Generic;
using System.Linq;
using Broiler.Dom;
using Broiler.JSeal;
using Broiler.HtmlBridge.Dom;
using Broiler.HtmlBridge.Logging;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// A form's submission as HTML's "submit" algorithm runs it for every way but <c>form.submit()</c> --
/// a submit button the user or a script clicked or the user pressed, Enter in a field,
/// <c>form.requestSubmit()</c> -- the constraint validation it starts, and a form's reset.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing validated a form.</b> A form with an empty required field was submitted like any other,
/// and <c>checkValidity()</c> asked only whether a <c>required</c> control's <c>value</c> attribute was
/// empty, whatever the user had typed. The answer is now the one <c>:invalid</c> gives, judged on the
/// live value by the selector matcher, so a page's own check, its styles and its submission agree.
/// </para>
/// <para>
/// <b>As Chromium submits, measured.</b> A submission marks every control of the form as interacted with
/// (<c>:user-valid</c>, <c>:user-invalid</c>), whether or not the form is valid. Unless the form has
/// <c>novalidate</c> or the submitter <c>formnovalidate</c>, each invalid control in tree order gets a
/// cancelable <c>invalid</c>, and the first whose <c>invalid</c> was not cancelled is focused; the form
/// is not submitted. Otherwise the form gets a trusted <c>submit</c> naming its submitter, and unless
/// that is cancelled the host is asked to submit it. <c>form.submit()</c> skips all of it
/// (Features/FormSubmitBinding.cs). A submission asked for while the form's own <c>submit</c> -- or its
/// validation -- is being fired is ignored, as is a reset asked for while its <c>reset</c> is.
/// </para>
/// <para>
/// <b>A submission's entry list is constructed here, with its <c>formdata</c> event</b> (HTML "constructing
/// the entry list"), as Chromium fires it, measured: after the <c>submit</c>, and for <c>form.submit()</c>
/// and <c>new FormData(form)</c> too, trusted, bubbling, its <c>formData</c> the form's entries with the
/// submitter's. What its listeners change is what is submitted. The host builds the request -- it holds
/// the files its pickers chose -- so the bridge hands it the submitter and the listeners' changes, which
/// it replays. While the listeners run, a submission of the form or a <c>new FormData</c> of it is
/// refused. A <c>method="dialog"</c> submission closes the form's dialog and navigates nothing, and one
/// into another browsing context -- a frame, a new window a script opens -- is not followed.
/// </para>
/// <para>
/// <b>A reset had two spellings that disagreed.</b> A reset button fired the form's <c>reset</c> and reset
/// it unless that was cancelled; <c>form.reset()</c> reset it without the event, so a page listening for
/// it, or cancelling it, heard nothing. Both are <see cref="ResetForm"/> now.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>
    /// Submits <paramref name="form"/> as <paramref name="submitter"/>, or the form itself, asks: marks
    /// its controls interacted with, validates it, fires its <c>submit</c>, and unless any of that stops
    /// it asks the host to submit it (<see cref="NavigationKind.FormSubmit"/>). Answers whether it did.
    /// </summary>
    private bool SubmitForm(DomElement form, DomElement? submitter, (int X, int Y) imagePoint = default)
    {
        // HTML "submit", "firing submission events": a requestSubmit() or a submit button's click from a
        // listener of this form's invalid or submit is ignored (Chromium, measured), and so is one from a
        // listener of its formdata ("constructing entry list").
        if (!form.IsConnected || _formsConstructingEntryList.Contains(form) || !_formsFiringSubmissionEvents.Add(form))
            return false;

        bool allowed;
        try
        {
            MarkFormInteracted(form);

            var validates = !HasAttr(form, "novalidate") && !(submitter is not null && HasAttr(submitter, "formnovalidate"));
            if (validates && !ValidateInteractively(form))
                return false;

            var realm = Realm;
            var evt = NewTrustedEvent(realm, "submit", bubbles: true, cancelable: true, composed: false, InterfacePrototype(realm, "SubmitEvent"));
            Define(realm, evt, "submitter", submitter is null ? JsValue.Null : WrapNode(submitter));
            allowed = DispatchKeyboardEvent(form, evt);
        }
        finally
        {
            _formsFiringSubmissionEvents.Remove(form);
        }

        if (!allowed || !form.IsConnected)
            return false;

        return SubmitEntryList(form, submitter, imagePoint);
    }

    /// <summary>
    /// <c>form.submit()</c>: HTML's "submit" run from the method -- no <c>submit</c> event and no validation,
    /// but the entry list is constructed, with its <c>formdata</c>.
    /// </summary>
    private void SubmitFromSubmitMethod(DomElement form)
    {
        if (form.IsConnected && !_formsConstructingEntryList.Contains(form))
            RunAsScriptCall(() => SubmitEntryList(form, submitter: null, imagePoint: default));
    }

    /// <summary>
    /// The rest of HTML's "submit", from constructing the entry list: the <c>formdata</c> event, then the
    /// dialog a <c>method="dialog"</c> form closes or the navigation the host is asked for. Answers whether
    /// the form went.
    /// </summary>
    private bool SubmitEntryList(DomElement form, DomElement? submitter, (int X, int Y) imagePoint)
    {
        if (ConstructEntryList(form, submitter, imagePoint, recordEdits: true) is not { } constructed || !form.IsConnected)
            return false;

        if (SubmissionMethodOf(form, submitter) == "dialog")
        {
            // The form's nearest dialog closes, its returnValue the submitter's value; nothing navigates.
            if (NearestAncestor(form, "dialog") is { } dialog)
                _dialogs.CloseDialog(dialog, DialogResultOf(submitter, imagePoint));
            return true;
        }

        var target = ResolveSubmissionTarget(form, submitter);
        if (target.Refused)
            return false;

        // A frame loads its own submissions (DomBridge/FrameSubmission.cs); the host submits the page's forms.
        if (target.Frame is { } frame)
        {
            SubmitIntoFrame(frame, form, submitter, imagePoint, constructed.Edits);
            return true;
        }

        if (GetFrameForContentDocument(GetOwningDocument(form)) is not null)
            return SubmitFromFrameIntoPage(form, submitter, imagePoint, constructed.Edits);

        RequestFormSubmission(form, submitter, imagePoint, constructed.Edits);
        return true;
    }

    /// <summary>
    /// HTML's "constructing the entry list": <paramref name="form"/>'s entries, with
    /// <paramref name="submitter"/>'s, in a <c>FormData</c> handed to the form's <c>formdata</c> listeners.
    /// With <paramref name="recordEdits"/>, what they do to it is recorded, for the host to replay. Null
    /// while the form is already constructing one.
    /// </summary>
    private (JsValue FormData, List<FormDataEdit> Edits)? ConstructEntryList(
        DomElement form, DomElement? submitter, (int X, int Y) imagePoint, bool recordEdits)
    {
        if (!_formsConstructingEntryList.Add(form))
            return null;

        try
        {
            var realm = Realm;
            var edits = new List<FormDataEdit>();
            var recording = recordEdits;
            var formData = _fetch.CreateFormData(realm, BuildFormEntryList(form, submitter, imagePoint), edit =>
            {
                if (recording)
                    edits.Add(edit);
            });

            var evt = NewTrustedEvent(realm, "formdata", bubbles: true, cancelable: false, composed: false, InterfacePrototype(realm, "FormDataEvent"));
            Define(realm, evt, "formData", formData);
            DispatchKeyboardEvent(form, evt);

            // The page may keep the FormData; what it does to it afterwards is not this submission's.
            recording = false;
            return (formData, edits);
        }
        finally
        {
            _formsConstructingEntryList.Remove(form);
        }
    }

    /// <summary>
    /// <c>new FormData(form, submitter)</c>: the form's constructed entry list, after its <c>formdata</c>;
    /// a <c>submitter</c> must be one of the form's submit buttons. Missing when <paramref name="candidate"/>
    /// is not a form.
    /// </summary>
    private JsValue FormDataForForm(JsValue candidate, JsValue submitterValue)
    {
        if (!candidate.IsObject || FindDomNodeByJSObject(candidate) is not DomElement form || !IsFormElement(form))
            return JsValue.Missing;

        DomElement? submitter = null;
        if (!submitterValue.IsNullish)
        {
            if (!_jsObjects.TryGetNode(submitterValue, out var node) || node is not DomElement button || !IsSubmitButton(button))
                throw Realm.Error(JsErrorKind.TypeError, "Failed to construct 'FormData': The specified element is not a submit button.");
            if (!ReferenceEquals(FormOwnerOf(button), form))
                throw Realm.DomError("NotFoundError", "Failed to construct 'FormData': The specified element is not owned by this form element.");
            submitter = button;
        }

        return RunAsScriptCall(() => ConstructEntryList(form, submitter, default, recordEdits: false))?.FormData
            ?? throw Realm.DomError("InvalidStateError", "Failed to construct 'FormData': The form is constructing an entry list.");
    }

    /// <summary>
    /// The submission's method: the submitter's <c>formmethod</c>, else the form's <c>method</c> --
    /// <c>get</c>, <c>post</c> or <c>dialog</c>, an invalid value being <c>get</c>.
    /// </summary>
    private static string SubmissionMethodOf(DomElement form, DomElement? submitter)
    {
        var declared = submitter is not null && TryGetAttribute(submitter, "formmethod", out var formMethod)
            ? formMethod
            : TryGetAttribute(form, "method", out var method) ? method : string.Empty;
        return declared.Trim().ToLowerInvariant() is ("post" or "dialog") and var known ? known : "get";
    }

    /// <summary>What a <c>method="dialog"</c> submission closes its dialog with: the submitter's value, an image button's "x,y", or nothing.</summary>
    private static string? DialogResultOf(DomElement? submitter, (int X, int Y) imagePoint) =>
        submitter is null ? null
        : InputTypeOf(submitter) == "image" && submitter.TagName.Equals("input", StringComparison.OrdinalIgnoreCase)
            ? $"{imagePoint.X},{imagePoint.Y}"
            : TryGetAttribute(submitter, "value", out var value) ? value : null;

    /// <summary>The <c>target</c> of the document's first <c>&lt;base&gt;</c> that has one, or empty.</summary>
    private static string BaseTargetOf(DomDocument document) =>
        document.Descendants().OfType<DomElement>()
            .FirstOrDefault(element => element.TagName.Equals("base", StringComparison.OrdinalIgnoreCase) && HasAttr(element, "target")) is { } baseElement &&
        TryGetAttribute(baseElement, "target", out var target) ? target : string.Empty;

    /// <summary>
    /// HTML's "reset" of a form, whoever asks -- a reset button the user or a script clicked, or
    /// <c>form.reset()</c>: a trusted, cancelable <c>reset</c> at it, and unless that is cancelled its
    /// controls reset. A reset asked for while its <c>reset</c> is being fired is ignored ("locked for reset").
    /// </summary>
    private void ResetForm(DomElement form)
    {
        if (!_formsResetting.Add(form))
            return;

        try
        {
            var realm = Realm;
            if (DispatchKeyboardEvent(form, NewTrustedEvent(realm, "reset", bubbles: true, cancelable: true, composed: false, InterfacePrototype(realm, "Event"))))
                ResetFormControls(form);
        }
        finally
        {
            _formsResetting.Remove(form);
        }
    }

    /// <summary>
    /// HTML's "interactively validate the constraints": <c>invalid</c> at each invalid control of
    /// <paramref name="form"/>, and the first whose <c>invalid</c> nobody cancelled focused. Answers
    /// whether the form is valid.
    /// </summary>
    private bool ValidateInteractively(DomElement form)
    {
        var invalid = InvalidControlsOf(form);
        if (invalid.Count == 0)
            return true;

        DomElement? report = null;
        foreach (var control in invalid)
        {
            if (FireInvalid(control) && report is null)
                report = control;
        }

        if (report is { IsConnected: true })
            MoveFocus(GetOwningDocument(report), report, FocusOrigin.Script);

        return false;
    }

    /// <summary>
    /// <c>checkValidity()</c>: for a control, whether it satisfies its constraints, with an
    /// <c>invalid</c> at it when it does not; for a form, whether all of its controls do, with an
    /// <c>invalid</c> at each that does not.
    /// </summary>
    private bool CheckValidity(DomElement element)
    {
        var invalid = IsFormElement(element) ? InvalidControlsOf(element) : IsInvalidControl(element) ? [element] : [];
        foreach (var control in invalid)
            FireInvalid(control);
        return invalid.Count == 0;
    }

    /// <summary><c>reportValidity()</c>: <see cref="CheckValidity"/>, then focus on the first invalid control whose <c>invalid</c> nobody cancelled.</summary>
    private bool ReportValidity(DomElement element)
    {
        var invalid = IsFormElement(element) ? InvalidControlsOf(element) : IsInvalidControl(element) ? [element] : [];
        DomElement? report = null;
        foreach (var control in invalid)
        {
            if (FireInvalid(control) && report is null)
                report = control;
        }

        if (report is { IsConnected: true })
            MoveFocus(GetOwningDocument(report), report, FocusOrigin.Script);
        return invalid.Count == 0;
    }

    /// <summary>
    /// <c>form.requestSubmit(submitter)</c>: a submission as <paramref name="submitter"/> -- one of the
    /// form's submit buttons -- or the form asks, validated, with its <c>submit</c>.
    /// </summary>
    private JsValue RequestSubmit(DomElement form, in JsCall call)
    {
        DomElement? submitter = null;
        if (call.Length > 0 && !call[0].IsNullish)
        {
            if (!_jsObjects.TryGetNode(call[0], out var node) || node is not DomElement button || !IsSubmitButton(button))
                throw Realm.Error(JsErrorKind.TypeError, "Failed to execute 'requestSubmit' on 'HTMLFormElement': The specified element is not a submit button.");
            if (!ReferenceEquals(FormOwnerOf(button), form))
                throw Realm.DomError("NotFoundError", "Failed to execute 'requestSubmit' on 'HTMLFormElement': The specified element is not owned by this form element.");
            submitter = button;
        }

        RunAsScriptCall(() => SubmitForm(form, submitter));
        return JsValue.Undefined;
    }

    /// <summary>
    /// The submit or reset button a user's click at <paramref name="target"/> activates -- the target,
    /// the button it is inside, or the button the label it is inside labels -- or null.
    /// </summary>
    private DomElement? FormButtonClickedAt(DomElement target)
    {
        for (var current = target; current != null; current = ParentEl(current))
        {
            if (current.TagName.Equals("a", StringComparison.OrdinalIgnoreCase) && HasAttr(current, "href"))
                return null;
            if (current.TagName.ToLowerInvariant() is "button" or "input")
                return IsSubmitButton(current) || IsResetButton(current) ? current : null;
            if (current.TagName.Equals("label", StringComparison.OrdinalIgnoreCase))
                return LabeledControlOf(current) is { } control && (IsSubmitButton(control) || IsResetButton(control)) ? control : null;
        }

        return null;
    }

    /// <summary>
    /// The activation of a submit or reset button a user's click reached: the form submitted or reset.
    /// Answers whether there was one.
    /// </summary>
    private bool ActivateFormButton(DomElement button, (int X, int Y) imagePoint = default)
    {
        if (IsDisabledFormControl(button) || FormOwnerOf(button) is not { } form)
            return false;

        if (IsSubmitButton(button))
            SubmitForm(form, button, imagePoint);
        else
            ResetForm(form);
        return true;
    }

    /// <summary>The controls of <paramref name="form"/> that do not satisfy their constraints, in tree order.</summary>
    private List<DomElement> InvalidControlsOf(DomElement form) =>
        CollectFormControlsIncludingCustom(form).Where(IsInvalidControl).ToList();

    /// <summary>
    /// Whether <paramref name="element"/> is a candidate for constraint validation that does not satisfy
    /// its constraints: what <c>:invalid</c> says of it, judged on its live value, or for a
    /// form-associated custom element what it set through its internals.
    /// </summary>
    private bool IsInvalidControl(DomElement element) =>
        CustomElements.IsFormAssociated(element)
            ? !ElementInternals.IsValid(element)
            : element.TagName.ToLowerInvariant() is "input" or "select" or "textarea" && _selectorMatcher.Matches(element, ":invalid");

    /// <summary>A cancelable <c>invalid</c> at <paramref name="control"/>; answers whether nobody cancelled it.</summary>
    private bool FireInvalid(DomElement control)
    {
        var realm = Realm;
        return DispatchKeyboardEvent(control,
            NewTrustedEvent(realm, "invalid", bubbles: false, cancelable: true, composed: false, InterfacePrototype(realm, "Event")));
    }

    private static bool IsFormElement(DomElement element) =>
        element.TagName.Equals("form", StringComparison.OrdinalIgnoreCase);

    // The forms whose submit event, or the validation before it, is being fired, those whose formdata
    // event is, and those whose reset event is: a submission or a reset of one of them asked for
    // meanwhile is ignored.
    private readonly HashSet<DomElement> _formsFiringSubmissionEvents = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<DomElement> _formsConstructingEntryList = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<DomElement> _formsResetting = new(ReferenceEqualityComparer.Instance);

    private void ResetFormSubmissionState()
    {
        _formsFiringSubmissionEvents.Clear();
        _formsConstructingEntryList.Clear();
        _formsResetting.Clear();
    }
}
