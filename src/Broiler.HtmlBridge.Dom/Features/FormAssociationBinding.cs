using System;
using System.Collections.Generic;
using Broiler.Dom;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Form association (HTML §4.10.2, §4.10.4): a control's <c>form</c> owner, a control's
/// <c>labels</c>, and a <c>&lt;label&gt;</c>'s <c>control</c> and <c>form</c>.
/// </summary>
/// <remarks>
/// <para>
/// All of it was <see langword="undefined"/>, and the two that matter are undefined in different
/// ways. <c>control.form</c> is how a script reaches the form from a control it was handed — an
/// event target, a query result — so the idiom <c>input.form.submit()</c> threw on the property
/// access rather than on the call. <c>control.labels</c> is how accessibility and validation code
/// finds the text describing a field; absent, a page that labels its own error messages had nothing
/// to read, and <c>labels.length</c> threw rather than answering zero.
/// </para>
/// <para>
/// <b>These are installed per tag, not on every element wrapper.</b> The bridge's other form members
/// (<c>value</c>, <c>checked</c>, <c>required</c>) sit on every element, which is a known deviation;
/// these deliberately do not, because their absence is observable and specified: a
/// <c>&lt;div&gt;</c> has no <c>labels</c> and no <c>form</c> property at all — reference-checked,
/// both are <c>undefined</c> in Chromium — while an <c>&lt;input type=hidden&gt;</c> <em>has</em>
/// <c>labels</c> and it is <c>null</c>. Three distinguishable states, so answering an empty list for
/// all of them would be wrong in two of them.
/// </para>
/// <para>
/// <b><c>label.form</c> follows the label's control, not the label's ancestry.</b> A label sitting
/// outside every form whose <c>for</c> points at a control inside one reports that form — checked
/// against Chromium, because the plausible reading (a label is itself a form-associated element, so
/// use its own position) gives <c>null</c> there and is wrong.
/// </para>
/// </remarks>
internal static class FormAssociationBinding
{
    /// <summary>
    /// The elements a <c>&lt;label&gt;</c> can label (HTML §4.10.4). <c>input</c> qualifies unless
    /// its type is <c>hidden</c>, which is the one case that reports <c>null</c> rather than an
    /// empty list.
    /// </summary>
    private static readonly HashSet<string> LabelableTags =
        new(StringComparer.OrdinalIgnoreCase) { "button", "input", "meter", "output", "progress", "select", "textarea" };

    /// <summary>
    /// The form-associated elements (HTML §4.10.2), which are the ones carrying a <c>form</c>
    /// property. Wider than the labelable set: <c>fieldset</c>, <c>object</c>, <c>img</c> and
    /// <c>label</c> associate with a form without being labelable.
    /// </summary>
    private static readonly HashSet<string> FormAssociatedTags =
        new(StringComparer.OrdinalIgnoreCase)
        { "button", "fieldset", "input", "label", "object", "output", "select", "textarea", "img" };

    public static void Install(IFormAssociationHost host, JSObject obj, DomElement element, string tag)
    {
        if (FormAssociatedTags.Contains(tag))
        {
            obj.FastAddProperty("form",
                new DomFunction((in _) => FormOwnerValue(host, element), "get form"),
                null, JSPropertyAttributes.EnumerableConfigurableProperty);
        }

        if (LabelableTags.Contains(tag))
        {
            obj.FastAddProperty("labels",
                new DomFunction((in _) => LabelsValue(host, element), "get labels"),
                null, JSPropertyAttributes.EnumerableConfigurableProperty);
        }

        if (string.Equals(tag, "label", StringComparison.OrdinalIgnoreCase))
        {
            obj.FastAddProperty("control",
                new DomFunction((in _) => LabeledControl(host, element) is { } control
                    ? host.ToJSObject(control)
                    : JSNull.Value, "get control"),
                null, JSPropertyAttributes.EnumerableConfigurableProperty);
        }
    }

    /// <summary>
    /// <c>element.form</c> — the form owner. For a <c>&lt;label&gt;</c> this is its control's form
    /// owner rather than its own (see the class remarks); for everything else it is the form named
    /// by the <c>form</c> content attribute, or the nearest ancestor <c>&lt;form&gt;</c>.
    /// </summary>
    private static JSValue FormOwnerValue(IFormAssociationHost host, DomElement element)
    {
        var subject = string.Equals(element.TagName, "label", StringComparison.OrdinalIgnoreCase)
            ? LabeledControl(host, element)
            : element;

        return subject is not null && FormOwner(host, subject) is { } form
            ? host.ToJSObject(form)
            : JSNull.Value;
    }

    /// <summary>
    /// The form owner of <paramref name="element"/> — the shared entry point, so a form-associated
    /// custom element's <c>ElementInternals.form</c> resolves it by the same rule an ordinary
    /// control's <c>form</c> does rather than by a second one.
    /// </summary>
    internal static DomElement? FormOwnerOf(IFormAssociationHost host, DomElement element) =>
        FormOwner(host, element);

    /// <summary>
    /// A control's live <c>labels</c> <c>NodeList</c>, without the hidden-input <c>null</c> case —
    /// the shape <c>ElementInternals.labels</c> reports, which is always a list.
    /// </summary>
    internal static JSValue LabelsNodeList(IFormAssociationHost host, DomElement element) =>
        DomCollectionBinding.NodeList(host.JsContext, () =>
        {
            var labels = new List<JSValue>();
            foreach (var candidate in host.Elements)
            {
                if (string.Equals(candidate.TagName, "label", StringComparison.OrdinalIgnoreCase) &&
                    ReferenceEquals(LabeledControl(host, candidate), element))
                    labels.Add(host.ToJSObject(candidate));
            }

            return labels;
        });

    private static DomElement? FormOwner(IFormAssociationHost host, DomElement element)
    {
        // The `form` content attribute wins over ancestry, and names a form by id anywhere in the
        // document — which is the whole point of it: a control rendered outside the form it submits.
        if (DomBridge.TryGetAttribute(element, "form", out var formId) && !string.IsNullOrEmpty(formId))
        {
            var named = host.GetElementById(formId);
            return named is not null && string.Equals(named.TagName, "form", StringComparison.OrdinalIgnoreCase)
                ? named
                : null;
        }

        for (var ancestor = DomBridge.ParentEl(element); ancestor is not null; ancestor = DomBridge.ParentEl(ancestor))
        {
            if (string.Equals(ancestor.TagName, "form", StringComparison.OrdinalIgnoreCase))
                return ancestor;
        }

        return null;
    }

    /// <summary>
    /// <c>control.labels</c> — a <b>live</b> <c>NodeList</c> of the labels associated with this
    /// control, in tree order, or <c>null</c> for a hidden input.
    /// </summary>
    private static JSValue LabelsValue(IFormAssociationHost host, DomElement element)
    {
        // An input whose type is hidden is not labelable, and the specified answer is null rather
        // than an empty list — a page can tell "this cannot be labelled" from "this is unlabelled".
        if (string.Equals(element.TagName, "input", StringComparison.OrdinalIgnoreCase) &&
            DomBridge.TryGetAttribute(element, "type", out var type) &&
            string.Equals(type, "hidden", StringComparison.OrdinalIgnoreCase))
            return JSNull.Value;

        return LabelsNodeList(host, element);
    }

    /// <summary>
    /// <c>label.control</c> — the control a label labels: the element its <c>for</c> attribute names
    /// when that element is labelable, and otherwise the first labelable element in the label's own
    /// subtree (HTML §4.10.4).
    /// </summary>
    /// <remarks>
    /// The <c>for</c> attribute is authoritative even when it names nothing: a label carrying
    /// <c>for</c> does <em>not</em> fall back to a descendant, so a label wrapping one control while
    /// pointing at a missing id labels nothing rather than quietly labelling what it wraps.
    /// </remarks>
    private static DomElement? LabeledControl(IFormAssociationHost host, DomElement label)
    {
        if (DomBridge.TryGetAttribute(label, "for", out var forId))
        {
            if (string.IsNullOrEmpty(forId))
                return null;

            var target = host.GetElementById(forId);
            return target is not null && IsLabelable(host, target) ? target : null;
        }

        foreach (var descendant in label.Descendants())
        {
            if (descendant is DomElement candidate && IsLabelable(host, candidate))
                return candidate;
        }

        return null;
    }

    private static bool IsLabelable(IFormAssociationHost host, DomElement element)
    {
        // A form-associated custom element is labelable (HTML §4.13.5) and no tag list can say so —
        // its tag is whatever the page named it.
        if (host.IsFormAssociatedCustomElement(element))
            return true;

        if (!LabelableTags.Contains(element.TagName))
            return false;

        return !string.Equals(element.TagName, "input", StringComparison.OrdinalIgnoreCase) ||
               !DomBridge.TryGetAttribute(element, "type", out var type) ||
               !string.Equals(type, "hidden", StringComparison.OrdinalIgnoreCase);
    }
}
