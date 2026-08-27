using Broiler.Dom;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge;

/// <summary>
/// A form's controls and its entry list (HTML §4.10.21.4) — the two questions that have to be
/// answered together once a form-associated custom element can be one of the controls.
/// </summary>
/// <remarks>
/// <para>
/// The canonical <c>HtmlElementQueries.CollectFormControls</c> matches on the four control tags,
/// which is right for the DOM it was written against and cannot answer for a custom element: its tag
/// is whatever the page named it, and only the custom-element registry knows whether its definition
/// declared <c>formAssociated</c>. So the collection is re-walked here, in the bridge, where that
/// registry is.
/// </para>
/// <para>
/// <b>The entry list existed nowhere.</b> <c>new FormData(form)</c> enumerated the <em>wrapper's</em>
/// own string properties, so it produced the element object's members — <c>tagName</c>,
/// <c>innerHTML</c> and the rest — instead of the form's fields. That made it useless for its only
/// idiom, and it is also the place a browser reads a form-associated custom element's submission
/// value, so <c>ElementInternals.setFormValue</c> would have had nowhere to be observed. Building the
/// list properly is what keeps that from being a shape-only stub.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>The four control tags, plus whatever the custom-element registry adds.</summary>
    private static readonly HashSet<string> ControlTags =
        new(StringComparer.Ordinal) { "input", "select", "textarea", "button" };

    /// <summary>
    /// <paramref name="form"/>'s controls in tree order, including its form-associated custom
    /// elements — what <c>form.elements</c> lists.
    /// </summary>
    internal List<DomElement> CollectFormControlsIncludingCustom(DomElement form)
    {
        var controls = new List<DomElement>();
        Collect(form);
        return controls;

        void Collect(DomElement parent)
        {
            foreach (var child in ChildElements(parent))
            {
                if (ControlTags.Contains(AsciiToLower(child.TagName)) ||
                    (_customElements?.IsFormAssociated(child) ?? false))
                    controls.Add(child);

                Collect(child);
            }
        }
    }

    /// <summary>
    /// <paramref name="form"/>'s entry list: each submittable control's name and current value, in
    /// tree order.
    /// </summary>
    /// <remarks>
    /// The exclusions are the specified ones and each is observable: a disabled control submits
    /// nothing, a control with no <c>name</c> submits nothing, an unchecked checkbox or radio submits
    /// nothing (and a checked one with no <c>value</c> submits <c>"on"</c>), and a button — including
    /// an <c>&lt;input type=submit&gt;</c> — submits only as the submitter, which a
    /// <c>new FormData(form)</c> has none of. A file input submits nothing because this engine has no
    /// file selection.
    /// </remarks>
    internal List<KeyValuePair<string, string>> BuildFormEntryList(DomElement form)
    {
        var entries = new List<KeyValuePair<string, string>>();
        foreach (var control in CollectFormControlsIncludingCustom(form))
        {
            if (IsFormControlDisabled(control))
                continue;

            var name = TryGetAttribute(control, "name", out var declaredName) ? declaredName : string.Empty;

            // A form-associated custom element's value is the one it set through its internals, and a
            // FormData submission value carries its own names — so it is asked before the name test.
            if (_customElements?.IsFormAssociated(control) == true)
            {
                if (_elementInternals?.SubmissionEntriesFor(control, name) is { } custom)
                    entries.AddRange(custom);
                continue;
            }

            if (string.IsNullOrEmpty(name))
                continue;

            var tag = AsciiToLower(control.TagName);
            switch (tag)
            {
                case "button":
                    continue;

                case "select":
                    entries.Add(new(name, _select.GetValue(control)));
                    continue;

                case "textarea":
                    entries.Add(new(name, CurrentControlValue(control, GetElementTextContent(control))));
                    continue;

                case "input":
                    AppendInputEntry(entries, control, name);
                    continue;
            }
        }

        return entries;
    }

    private void AppendInputEntry(List<KeyValuePair<string, string>> entries, DomElement input, string name)
    {
        var type = TryGetAttribute(input, "type", out var declaredType)
            ? AsciiToLower(declaredType)
            : "text";

        switch (type)
        {
            case "submit" or "reset" or "button" or "image" or "file":
                return;

            case "checkbox" or "radio":
                if (!IsControlChecked(input))
                    return;
                entries.Add(new(name, TryGetAttribute(input, "value", out var boxValue) ? boxValue : "on"));
                return;

            default:
                entries.Add(new(name, CurrentControlValue(input,
                    TryGetAttribute(input, "value", out var attributeValue) ? attributeValue : string.Empty)));
                return;
        }
    }

    /// <summary>The control's current value: its dirty IDL value when it has one, and
    /// <paramref name="fallback"/> — the markup's default — when it does not.</summary>
    private string CurrentControlValue(DomElement control, string fallback) =>
        FormControlStateFor(control).Value.TryGet(out var stored) && stored is string value
            ? value
            : fallback;

    private bool IsControlChecked(DomElement control) =>
        FormControlStateFor(control).Checked.TryGet(out var stored)
            ? stored is true
            : HasAttr(control, "checked");

    /// <summary>
    /// Whether the control is disabled — by its own <c>disabled</c> attribute or by an ancestor
    /// <c>&lt;fieldset disabled&gt;</c>, which disables everything in it (HTML §4.10.15).
    /// </summary>
    internal static bool IsFormControlDisabled(DomElement control)
    {
        if (HasAttr(control, "disabled"))
            return true;

        for (var ancestor = ParentEl(control); ancestor is not null; ancestor = ParentEl(ancestor))
        {
            if (string.Equals(ancestor.TagName, "fieldset", StringComparison.OrdinalIgnoreCase) &&
                HasAttr(ancestor, "disabled"))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Reads a <c>FormData</c>'s entries, or answers <see langword="false"/> for anything else.
    /// </summary>
    /// <remarks>
    /// Recognised by shape rather than by identity, because this engine's <c>FormData</c> objects are
    /// plain objects carrying the interface's members rather than instances of a registered
    /// interface. Reading through <c>forEach</c> rather than the private entry list keeps this
    /// working for any object that really is one.
    /// </remarks>
    internal static bool TryReadFormDataEntries(JSObject candidate, out List<KeyValuePair<string, string>> entries)
    {
        entries = [];
        if (candidate[(KeyString)"forEach"] is not JSFunction forEach ||
            candidate[(KeyString)"append"] is not JSFunction ||
            candidate[(KeyString)"getAll"] is not JSFunction)
            return false;

        var collected = entries;
        var collector = new DomFunction((in a) =>
        {
            // forEach hands (value, name, formData), the order the Web IDL iterable declares.
            if (a.Length >= 2)
                collected.Add(new KeyValuePair<string, string>(a[1].ToString(), a[0].ToString()));
            return JSUndefined.Value;
        }, "collect", 3);

        forEach.InvokeFunction(new Arguments(candidate, collector));
        return true;
    }
}
