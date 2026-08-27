using Broiler.Dom;
using Broiler.Dom.Html;

namespace Broiler.HtmlBridge;

/// <summary>
/// The form-reset algorithm (HTML §4.10.21.4) and the radio-button group invariant it depends on.
/// </summary>
/// <remarks>
/// <para>
/// A reset is defined entirely in terms of the <em>dirty flags</em> a control carries: resetting an
/// <c>&lt;input&gt;</c> means clearing its dirty value and dirty checkedness flags, after which the
/// value and checkedness track the <c>value</c> and <c>checked</c> content attributes again. The
/// bridge already keeps exactly those flags — the per-element <c>FormControl</c> runtime slots, whose
/// unset state is what the IDL getters fall back through — so the algorithm is a matter of removing
/// them rather than of computing replacement values. That is why this can be a small amount of code
/// for a specified operation: the state model was already right, and only the operation on it was
/// missing.
/// </para>
/// <para>
/// The radio invariant is not part of resetting as such, but a reset is one of the moments that can
/// break it: a group whose markup carries <c>checked</c> on more than one member has every one of
/// them restored, and "at most one member of a radio button group is checked" has to be re-imposed
/// afterwards. The same invariant is broken by <em>insertion</em> — appending an already-checked
/// radio into a group that has one — which is why <see cref="EnforceRadioGroupExclusivity"/> is
/// shared with the insertion path rather than kept private here.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>
    /// Resets <paramref name="form"/>'s controls (HTML §4.10.21.4). Each resettable control's dirty
    /// flags are cleared so its state tracks its markup again, then the radio invariant is
    /// re-imposed and the style scope invalidated — <c>:checked</c> and the value-dependent
    /// selectors are cascade inputs.
    /// </summary>
    internal void ResetFormControls(DomElement form)
    {
        var controls = HtmlElementQueries.CollectFormControls(form);
        foreach (var control in controls)
            ResetFormControl(control);

        EnforceRadioGroupExclusivity(form);
        InvalidateStyleScope(form);

        // A form-associated custom element has no dirty flags to clear — its value is whatever it
        // chose to submit — so a reset reaches it as a reaction instead, which is where a component
        // restores its own default.
        _customElements?.OnFormReset(CollectFormControlsIncludingCustom(form));
    }

    /// <summary>
    /// One control's reset algorithm. Every case is a dirty-flag removal, because the flag being
    /// unset <em>is</em> "tracks the markup" everywhere the IDL getters read it.
    /// </summary>
    private void ResetFormControl(DomElement control)
    {
        var state = FormControlStateFor(control);
        switch (control.TagName?.ToLowerInvariant())
        {
            case "input":
                // Dirty value flag and dirty checkedness flag, both cleared: value falls back to the
                // `value` attribute and checkedness to the presence of `checked`.
                state.Value.Remove();
                state.Checked.Remove();
                break;

            case "textarea":
                // A textarea has no `value` attribute — its default is the child text content, which
                // is where the value getter falls back to once this flag is gone.
                state.Value.Remove();
                break;

            case "select":
                // "Set the selectedness of each option to its selectedness content attribute" — the
                // select's own dirty index is what overrides that, so removing it restores the
                // markup's selection. An option's dirty selectedness (set through
                // `option.selected`/`defaultSelected`) is cleared with it, so a script-selected
                // option does not survive the reset that a markup-selected one must.
                state.SelectedIndex.Remove();
                foreach (var option in HtmlElementQueries.CollectFormControls(control))
                {
                    if (string.Equals(option.TagName, "option", StringComparison.OrdinalIgnoreCase))
                        FormControlStateFor(option).DefaultSelected.Remove();
                }

                break;
        }
    }

    /// <summary>
    /// Re-imposes "at most one member of a radio button group is checked" over every group under
    /// <paramref name="scope"/>, keeping the <b>last</b> checked member in tree order.
    /// </summary>
    /// <remarks>
    /// Last rather than first, because that is what the specification's own ordering produces and
    /// what a browser answers. The rule fires whenever a radio's checkedness becomes true, so
    /// restoring or inserting a run of checked radios one at a time leaves each one unchecking the
    /// ones before it — the final state is the last one processed. Reference-checked against
    /// Chromium for both the reset case (two <c>checked</c> radios in the markup) and the insertion
    /// case (appending a checked radio into a group that already has one).
    /// </remarks>
    private void EnforceRadioGroupExclusivity(DomElement scope)
    {
        // Group name -> the last checked radio seen for it. Radios with no name are not in a group
        // (HTML: the group is the elements sharing a non-empty name), so they are left alone.
        Dictionary<string, DomElement>? lastChecked = null;

        foreach (var element in scope.InclusiveDescendants().OfType<DomElement>())
        {
            if (!IsRadioInput(element) ||
                !TryGetAttribute(element, "name", out var name) ||
                string.IsNullOrEmpty(name) ||
                !IsCheckedNow(element))
                continue;

            lastChecked ??= [];
            if (lastChecked.TryGetValue(name, out var previous))
                FormControlStateFor(previous).Checked.Set(false);
            lastChecked[name] = element;
        }
    }

    /// <summary>The checkedness a radio reports right now: its dirty flag when set, else the
    /// presence of the <c>checked</c> content attribute — the same fallback the IDL getter uses.</summary>
    private bool IsCheckedNow(DomElement element) =>
        FormControlStateFor(element).Checked.TryGet(out var dirty)
            ? dirty is true
            : HasAttr(element, "checked");

    private static bool IsRadioInput(DomElement element) =>
        string.Equals(element.TagName, "input", StringComparison.OrdinalIgnoreCase) &&
        TryGetAttribute(element, "type", out var type) &&
        string.Equals(type, "radio", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The radio-group scope for <paramref name="element"/>: its form owner, or the root of its tree
    /// when it has none (HTML defines the group over the form owner, falling back to the tree).
    /// </summary>
    private static DomElement RadioGroupScope(DomElement element)
    {
        var scope = ParentEl(element);
        while (scope != null && !string.Equals(scope.TagName, "form", StringComparison.OrdinalIgnoreCase))
            scope = ParentEl(scope);

        if (scope != null)
            return scope;

        scope = element;
        while (ParentEl(scope) != null)
            scope = ParentEl(scope);
        return scope;
    }

    /// <summary>
    /// Restores the radio invariant after <paramref name="inserted"/> (or a descendant of it) joins
    /// the tree. An already-checked radio that is appended into a group with a checked member left
    /// two checked, which is a state a browser never shows and a state no user interaction can
    /// produce — a form serialized in it submits two values for one field.
    /// </summary>
    /// <remarks>
    /// This runs on every element insertion, so its cost matters. It is a walk of the inserted
    /// subtree with a tag comparison per element, and it is the <em>third</em> such walk on this
    /// line — <c>FireDescendantOnloads</c> and <c>FireDescendantStylesheetLinkLoads</c> already
    /// traverse the same subtree with per-element predicates of the same order. So it is a constant
    /// factor on a path that is already O(subtree), not a new order of growth. A subtree carrying no
    /// checked radio does the walk and nothing else; there is no cheaper signal, because whether a
    /// radio is checked is exactly what has to be looked at.
    /// </remarks>
    private void EnforceRadioGroupExclusivityForInsertion(DomElement inserted)
    {
        foreach (var element in inserted.InclusiveDescendants().OfType<DomElement>())
        {
            if (IsRadioInput(element) && IsCheckedNow(element) &&
                TryGetAttribute(element, "name", out var name) && !string.IsNullOrEmpty(name))
            {
                // The newcomer wins, which is what "checkedness set to true" gives it: it is the
                // last member of the group to have been made checked.
                UncheckRadioSiblings(RadioGroupScope(element), element, name);
            }
        }
    }
}
