using Broiler.Dom;
using Broiler.JSeal;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// Input-method composition in the focused text field: <c>compositionstart</c>,
/// <c>compositionupdate</c> and <c>compositionend</c>, and the edits it makes, which the page hears as
/// <c>beforeinput</c> and <c>input</c> of type <c>insertCompositionText</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>A page heard nothing of a composition.</b> Text typed through an input method -- Japanese,
/// Chinese, Korean, or an accent built from a dead key -- reached the page, if at all, as the committed
/// characters, and a page that waits for <c>compositionend</c> before it acts on a field (an
/// autocomplete, React's <c>onChange</c>) never acted.
/// </para>
/// <para>
/// <b>The order is the UI Events and Input Events specifications', as Chromium implements them</b> --
/// it could not be measured here, since nothing drives an input method in the browser this bridge's
/// other behaviour was measured in. A composition starts with <c>compositionstart</c>, whose
/// <c>data</c> is the text it will replace. Each change is <c>compositionupdate</c>, then a
/// <c>beforeinput</c> that cannot be cancelled and an <c>input</c>, both <c>insertCompositionText</c>
/// with <c>isComposing</c>, around the field taking the text: the field's value holds what is being
/// composed, as Chromium's does. The commit makes the last of those edits and then ends with
/// <c>compositionend</c>; a cancelled composition puts the field's value back and ends with an empty
/// one. Keys pressed while composing have <c>isComposing</c> too.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>A composition under way: in which field, what it replaces, and what it holds now.</summary>
    private sealed class CompositionState(DomElement field, string baseValue, int start, int end)
    {
        public DomElement Field { get; } = field;

        /// <summary>The field's value before the composition began.</summary>
        public string BaseValue { get; } = baseValue;

        /// <summary>Where the text being composed goes: the selection when it began.</summary>
        public int Start { get; } = start;

        public int End { get; } = end;

        /// <summary>The text composed so far.</summary>
        public string Text { get; set; } = string.Empty;
    }

    private CompositionState? _composition;

    /// <summary>Delivers a step of an input method's composition in the focused text field.</summary>
    internal KeyboardInputResult DispatchComposition(CompositionInput input)
    {
        if (_realm is null)
            return default;

        switch (input.Kind)
        {
            case CompositionInputKind.Start:
                return StartComposition() is null ? default : new KeyboardInputResult(true, false);

            case CompositionInputKind.Update:
                if ((_composition ?? StartComposition()) is not { } updating)
                    return default;
                UpdateComposition(updating, input.Text ?? string.Empty, input.EditedValue);
                return new KeyboardInputResult(true, false);

            case CompositionInputKind.Commit:
                if ((_composition ?? StartComposition()) is not { } committing)
                    return default;
                var text = input.Text ?? string.Empty;
                if (!string.Equals(text, committing.Text, StringComparison.Ordinal))
                    FireCompositionEvent(committing.Field, "compositionupdate", text);
                EditComposition(committing, text, input.EditedValue);
                EndComposition(committing, text);
                return new KeyboardInputResult(true, false);

            default:
                if (_composition is not { } cancelled)
                    return default;
                if (cancelled.Text.Length > 0)
                    FireCompositionEvent(cancelled.Field, "compositionupdate", string.Empty);
                EditComposition(cancelled, string.Empty, cancelled.BaseValue);
                EndComposition(cancelled, string.Empty);
                return new KeyboardInputResult(true, false);
        }
    }

    /// <summary>Begins a composition in the focused text field: <c>compositionstart</c>, naming the text it replaces.</summary>
    private CompositionState? StartComposition()
    {
        if (FocusedElementIn(FocusedDocument) is not { } field || !IsEditableTextField(field))
            return null;

        var value = _formState.GetEffectiveValue(field);
        var selection = SelectionApplies(field) ? SelectionOf(field) : new FieldSelectionRange(value.Length, value.Length, "forward");
        var composition = new CompositionState(field, value, selection.Start, selection.End);
        _composition = composition;
        FireCompositionEvent(field, "compositionstart", value[selection.Start..selection.End], cancelable: true);
        return composition;
    }

    private void UpdateComposition(CompositionState composition, string text, string? editedValue)
    {
        FireCompositionEvent(composition.Field, "compositionupdate", text);
        EditComposition(composition, text, editedValue);
    }

    /// <summary>
    /// The field takes the composed text in place of what the composition replaces: a <c>beforeinput</c>
    /// that cannot be cancelled, the value, an <c>input</c>; the caret after the text.
    /// </summary>
    private void EditComposition(CompositionState composition, string text, string? editedValue)
    {
        composition.Text = text;
        var field = composition.Field;
        if (!field.IsConnected)
            return;

        FireEditEvent(field, "beforeinput", "insertCompositionText", text);
        var value = editedValue ?? composition.BaseValue[..composition.Start] + text + composition.BaseValue[composition.End..];
        ApplyUserEdit(field, value, "insertCompositionText", text);
        if (SelectionApplies(field))
        {
            var caret = Math.Min(composition.Start + text.Length, value.Length);
            SetSelection(field, caret, caret, "none");
        }
    }

    private void EndComposition(CompositionState composition, string text)
    {
        _composition = null;
        if (composition.Field.IsConnected)
            FireCompositionEvent(composition.Field, "compositionend", text);
    }

    /// <summary>A trusted composition event at <paramref name="field"/>, carrying <paramref name="data"/>.</summary>
    private void FireCompositionEvent(DomElement field, string type, string data, bool cancelable = false)
    {
        var realm = Realm;
        var evt = NewTrustedEvent(realm, type, bubbles: true, cancelable, composed: true, InterfacePrototype(realm, "CompositionEvent"));
        var window = WindowOfDocument(GetOwningDocument(field));
        Define(realm, evt, "view", window.IsObject ? window : JsValue.Null);
        Define(realm, evt, "detail", JsValue.Number(0));
        Define(realm, evt, "data", JsValue.String(data));
        DispatchKeyboardEvent(field, evt);
    }
}
