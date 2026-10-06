using System.Collections.Generic;
using Broiler.Dom;
using Broiler.JSeal;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// A text field's selection: <c>selectionStart</c>, <c>selectionEnd</c>, <c>selectionDirection</c>,
/// <c>setSelectionRange()</c>, <c>select()</c> and <c>setRangeText()</c>, the <c>select</c> and
/// <c>selectionchange</c> events, and the selection a host's editor reports (<see cref="DispatchSelection"/>).
/// </summary>
/// <remarks>
/// <para>
/// <b>None of it existed.</b> <c>input.selectionStart</c> was <c>undefined</c> and
/// <c>input.setSelectionRange(…)</c> a <c>TypeError</c> that ended the script calling it -- an input
/// mask putting the caret back after reformatting a value, a search box selecting its text on focus. And
/// the window's editor kept its caret and selection to itself.
/// </para>
/// <para>
/// <b>As Chromium has it, measured.</b> A field's selection starts at 0, 0, <c>forward</c> (an unset
/// direction reads <c>forward</c>, as on Windows). A value a script sets moves the caret to its end; a
/// Tab into an input selects all of it, into a text area keeps its selection. <c>setSelectionRange</c>
/// clamps to the value, pulls a start past the end back to it, and reads any direction but
/// <c>backward</c> as <c>forward</c>. A change a script makes fires no <c>select</c>, only a
/// <c>selectionchange</c> at the field, which bubbles, comes in a later task, and comes once however
/// many changes that task made. A selection the user makes fires a trusted <c>select</c> each time it
/// selects something, and a <c>selectionchange</c> too. The selection API applies to text, search,
/// URL, telephone and password inputs and to text areas; for an email or number input the three
/// properties are <c>null</c>, setting them or calling <c>setSelectionRange</c> throws
/// <c>InvalidStateError</c>, and <c>select()</c> does nothing.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>A field's selection, in UTF-16 code units of its value.</summary>
    private readonly record struct FieldSelectionRange(int Start, int End, string Direction);

    private readonly Dictionary<DomElement, FieldSelectionRange> _fieldSelections = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<DomElement> _selectionChangeScheduled = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// A number that changes whenever a script changes a text field's value or selection, so that a
    /// host editing the focused field with an editor of its own reads <see cref="GetFocusedTextField"/>
    /// again and follows it.
    /// </summary>
    internal long FieldVersion { get; private set; }

    /// <summary>Installs the selection API on a text field's object.</summary>
    private void InstallFieldSelection(JsValue handle, DomElement element)
    {
        var realm = Realm;
        realm.DefineAccessor(handle, "selectionStart",
            (in _) => SelectionApplies(element) ? JsValue.Number(SelectionOf(element).Start) : JsValue.Null,
            (in call) => SetSelectionPart(element, in call, "selectionStart"));
        realm.DefineAccessor(handle, "selectionEnd",
            (in _) => SelectionApplies(element) ? JsValue.Number(SelectionOf(element).End) : JsValue.Null,
            (in call) => SetSelectionPart(element, in call, "selectionEnd"));
        realm.DefineAccessor(handle, "selectionDirection",
            (in _) => SelectionApplies(element) ? JsValue.String(SelectionOf(element).Direction) : JsValue.Null,
            (in call) => SetSelectionPart(element, in call, "selectionDirection"));

        realm.DefineMethod(handle, "setSelectionRange", 2, (in call) =>
        {
            RequireSelection(element, "setSelectionRange");
            var length = ValueLength(element);
            SetSelectionByScript(element, Offset(call, 0, length), Offset(call, 1, length),
                call.Length > 2 && !call[2].IsUndefined ? call.Realm.ToJsString(call[2]) : "none");
            return JsValue.Undefined;
        });

        realm.DefineMethod(handle, "select", 0, (in _) =>
        {
            if (SelectionApplies(element))
                SetSelectionByScript(element, 0, ValueLength(element), "none");
            return JsValue.Undefined;
        });

        realm.DefineMethod(handle, "setRangeText", 1, (in call) => SetRangeText(element, in call));
    }

    /// <summary>Whether the selection API applies to <paramref name="element"/>: a text area, or a text, search, URL, telephone or password input.</summary>
    private static bool SelectionApplies(DomElement element) =>
        element.TagName.Equals("textarea", StringComparison.OrdinalIgnoreCase) ||
        element.TagName.Equals("input", StringComparison.OrdinalIgnoreCase) && InputTypeOf(element) is "" or "text" or "search" or "url" or "tel" or "password";

    private void RequireSelection(DomElement element, string member)
    {
        if (!SelectionApplies(element))
            throw Realm.DomError("InvalidStateError",
                $"Failed to execute '{member}' on 'HTMLInputElement': The input element's type ('{InputTypeOf(element)}') does not support selection.");
    }

    private int ValueLength(DomElement element) => _formState.GetEffectiveValue(element).Length;

    /// <summary>The field's selection, clamped to its value as it is now.</summary>
    private FieldSelectionRange SelectionOf(DomElement element)
    {
        var length = ValueLength(element);
        var selection = _fieldSelections.TryGetValue(element, out var stored) ? stored : new FieldSelectionRange(0, 0, "forward");
        var start = Math.Min(selection.Start, length);
        return selection with { Start = start, End = Math.Clamp(selection.End, start, length) };
    }

    /// <summary>An offset argument as Web IDL's <c>unsigned long</c> reads it, clamped to the value's length.</summary>
    private static int Offset(in JsCall call, int index, int length)
    {
        var number = call.Length > index ? call.Realm.ToNumber(call[index]) : 0;
        var offset = double.IsFinite(number) ? (uint)(long)Math.Truncate(number) : 0u;
        return (int)Math.Min(offset, (uint)length);
    }

    private JsValue SetSelectionPart(DomElement element, in JsCall call, string part)
    {
        if (!SelectionApplies(element))
            throw Realm.DomError("InvalidStateError",
                $"Failed to set the '{part}' property on 'HTMLInputElement': The input element's type ('{InputTypeOf(element)}') does not support selection.");

        var current = SelectionOf(element);
        var length = ValueLength(element);
        var value = call.Length > 0 ? call[0] : JsValue.Undefined;
        switch (part)
        {
            case "selectionStart":
                var start = OffsetOf(call.Realm, value, length);
                SetSelectionByScript(element, start, Math.Max(start, current.End), current.Direction);
                break;
            case "selectionEnd":
                SetSelectionByScript(element, current.Start, OffsetOf(call.Realm, value, length), current.Direction);
                break;
            default:
                // Measured: a collapsed selection keeps its direction whatever is asked of it.
                if (current.Start != current.End)
                    SetSelectionByScript(element, current.Start, current.End, call.Realm.ToJsString(value));
                break;
        }

        return JsValue.Undefined;
    }

    private static int OffsetOf(IJsRealm realm, JsValue value, int length)
    {
        var number = realm.ToNumber(value);
        var offset = double.IsFinite(number) ? (uint)(long)Math.Truncate(number) : 0u;
        return (int)Math.Min(offset, (uint)length);
    }

    /// <summary>
    /// <c>setRangeText(replacement)</c> and <c>setRangeText(replacement, start, end, selectionMode)</c>:
    /// replaces part of the value, and selects as <c>select</c>, <c>start</c>, <c>end</c> or
    /// <c>preserve</c> -- the default -- asks (HTML §4.10.20.5).
    /// </summary>
    private JsValue SetRangeText(DomElement element, in JsCall call)
    {
        RequireSelection(element, "setRangeText");
        var realm = call.Realm;
        var replacement = call.Length > 0 ? realm.ToJsString(call[0]) : "undefined";
        var value = _formState.GetEffectiveValue(element);
        var selection = SelectionOf(element);

        int start, end;
        var mode = "preserve";
        if (call.Length < 2)
        {
            start = selection.Start;
            end = selection.End;
        }
        else
        {
            var rawStart = realm.ToNumber(call[1]);
            var rawEnd = call.Length > 2 ? realm.ToNumber(call[2]) : 0;
            if (rawStart > rawEnd)
                throw realm.DomError("IndexSizeError",
                    "Failed to execute 'setRangeText' on 'HTMLInputElement': The provided start value is larger than the provided end value.");
            start = OffsetOf(realm, call[1], value.Length);
            end = call.Length > 2 ? OffsetOf(realm, call[2], value.Length) : start;
            if (call.Length > 3 && !call[3].IsUndefined)
                mode = realm.ToJsString(call[3]);
        }

        var newValue = value[..start] + replacement + value[end..];
        var newEnd = start + replacement.Length;
        var delta = replacement.Length - (end - start);

        (int Start, int End) newSelection = mode switch
        {
            "select" => (start, newEnd),
            "start" => (start, start),
            "end" => (newEnd, newEnd),
            _ => (Preserve(selection.Start, start, end, delta, start), Preserve(selection.End, start, end, delta, newEnd)),
        };

        _formState.SetDirtyValue(element, newValue);
        NoteScriptSetFieldValue(element, newValue);
        if (!string.Equals(newValue, value, StringComparison.Ordinal))
            FieldVersion++;
        SetSelectionByScript(element, newSelection.Start, newSelection.End, "none");
        return JsValue.Undefined;

        static int Preserve(int offset, int start, int end, int delta, int moved) =>
            offset > end ? offset + delta : offset > start ? moved : offset;
    }

    /// <summary>A script moved the selection: no <c>select</c>, a <c>selectionchange</c> when it changed.</summary>
    private void SetSelectionByScript(DomElement element, int start, int end, string direction)
    {
        if (SetSelection(element, start, end, direction))
            FieldVersion++;
    }

    /// <summary>
    /// Sets the selection, a start past the end pulled back to it, and schedules its
    /// <c>selectionchange</c>. Answers whether it changed.
    /// </summary>
    private bool SetSelection(DomElement element, int start, int end, string direction)
    {
        var length = ValueLength(element);
        end = Math.Clamp(end, 0, length);
        start = Math.Clamp(start, 0, end);
        var normalized = direction == "backward" ? "backward" : "forward";
        var next = new FieldSelectionRange(start, end, normalized);
        if (SelectionOf(element) == next)
            return false;

        _fieldSelections[element] = next;
        ScheduleSelectionChange(element);
        return true;
    }

    /// <summary>A <c>selectionchange</c> at the field in a later task, one however many changes come before it.</summary>
    private void ScheduleSelectionChange(DomElement element)
    {
        if (!_selectionChangeScheduled.Add(element))
            return;

        QueueFrameAction(() =>
        {
            _selectionChangeScheduled.Remove(element);
            if (_realm is null || !element.IsConnected)
                return;

            var realm = Realm;
            DispatchKeyboardEvent(element,
                NewTrustedEvent(realm, "selectionchange", bubbles: true, cancelable: false, composed: false, InterfacePrototype(realm, "Event")));
        });
    }

    /// <summary>A script set the field's value: when it changed, the caret goes to its end (Chromium, measured).</summary>
    private void MoveCaretToEndAfterScriptValue(DomElement element, string previous, string value)
    {
        if (string.Equals(previous, value, StringComparison.Ordinal) || !SelectionApplies(element))
            return;

        SetSelectionByScript(element, value.Length, value.Length, "none");
    }

    /// <summary>A Tab moved focus to <paramref name="field"/>: an input's whole value is selected, a text area keeps its selection.</summary>
    private void SelectOnKeyboardFocus(DomElement field)
    {
        if (!SelectionApplies(field))
            return;

        if (field.TagName.Equals("input", StringComparison.OrdinalIgnoreCase))
            SetSelectionByScript(field, 0, ValueLength(field), "none");
        else
            ScheduleSelectionChange(field);
    }

    /// <summary>
    /// The user selected in the focused text field with the host's editor -- a drag, Shift and an arrow,
    /// a click that placed the caret: a trusted <c>select</c> when it selects something new, and a
    /// <c>selectionchange</c>.
    /// </summary>
    internal KeyboardInputResult DispatchFieldSelection(FieldSelectionInput input)
    {
        if (_realm is null || FocusedElementIn(FocusedDocument) is not { } field || !IsEditableTextField(field))
            return default;

        if (!SelectionApplies(field))
            return new KeyboardInputResult(true, false);

        var changed = SetSelection(field, input.Start, input.End, input.Backward ? "backward" : "forward");
        var selection = SelectionOf(field);
        if (changed && selection.Start != selection.End)
        {
            var realm = Realm;
            DispatchKeyboardEvent(field,
                NewTrustedEvent(realm, "select", bubbles: true, cancelable: false, composed: false, InterfacePrototype(realm, "Event")));
        }

        return new KeyboardInputResult(true, false);
    }

    /// <summary>The user's edit put the caret after what it inserted, which is the end of the value unless the host's editor says otherwise.</summary>
    private void PlaceCaretAfterUserEdit(DomElement field)
    {
        if (SelectionApplies(field))
            SetSelection(field, ValueLength(field), ValueLength(field), "none");
    }

    private void ResetFieldSelections()
    {
        _fieldSelections.Clear();
        _selectionChangeScheduled.Clear();
        FieldVersion = 0;
    }
}
