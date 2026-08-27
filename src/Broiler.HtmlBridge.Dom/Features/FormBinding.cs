using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.Dom;
using Broiler.Dom.Html;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The HTMLFormElement feature binding (HtmlBridge complexity-reduction roadmap Phase 3, P3.9) —
/// <c>form.elements</c> (an <c>HTMLFormControlsCollection</c> with numeric and named access),
/// <c>form.length</c>, <c>form.action</c>, and the constraint-validation checks
/// (<c>checkValidity</c>/<c>reportValidity</c>) that the bridge exposes on form-associated elements.
/// Control collection and validity are pure tree/attribute work over the assembly's static
/// <c>DomBridge</c> helpers (<c>CollectFormControls</c>, <c>HasAttr</c>/<c>TryGetAttribute</c>,
/// <c>ChildElements</c>/<c>IsText</c>); the only bridge coupling — wrapping a control as a JS object —
/// goes through the narrow <see cref="IFormHost"/> contract.
/// </summary>
internal sealed class FormBinding(IFormHost host)
{
    private readonly IFormHost _host = host;

    /// <summary>Installs the <c>HTMLFormElement</c> members on <paramref name="obj"/> when
    /// <paramref name="element"/> is a <c>&lt;form&gt;</c>.</summary>
    internal void Install(JSObject obj, DomElement element, string tag)
    {
        if (tag != "form")
            return;

        // elements — the form controls collection (numeric + named access)
        obj.FastAddProperty("elements",
            new DomFunction((in _) => BuildElementsCollection(element), "get elements"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);
        // length — alias for elements.length
        obj.FastAddProperty("length",
            new DomFunction((in _) => new JSNumber(_host.CollectFormControls(element).Count), "get length"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);
        // action (read/write)
        obj.FastAddProperty("action",
            new DomFunction((in _) => DomBridge.TryGetAttribute(element, "action", out var act) ? new JSString(act) : new JSString(string.Empty), "get action"),
            new DomFunction((in a) => SetAction(element, in a), "set action"),
            JSPropertyAttributes.EnumerableConfigurableProperty);
        // reset() — HTML §4.10.21.4. It did not exist, so `form.reset()` was a TypeError on
        // undefined: the call that a "clear this form" control is written as aborted the handler
        // rather than clearing anything, and every edited control kept its edited state.
        obj.FastAddValue("reset",
            new DomFunction((in _) => { _host.ResetForm(element); return JSUndefined.Value; }, "reset", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);
    }

    private JSObject BuildElementsCollection(DomElement form)
    {
        var controls = _host.CollectFormControls(form);

        // FormElementsCollection returns null for missing named properties (per
        // HTMLFormControlsCollection spec behaviour).
        var collection = new FormElementsCollection(form, _host);
        for (int i = 0; i < controls.Count; i++)
            collection.FastAddValue((uint)i, _host.ToJSObject(controls[i]),
                JSPropertyAttributes.EnumerableConfigurableValue);

        collection.FastAddProperty("length",
            new DomFunction((in _) => new JSNumber(_host.CollectFormControls(form).Count), "get length"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        return collection;
    }

    private static JSValue SetAction(DomElement element, in Arguments a)
    {
        DomBridge.SetAttr(element, "action", a.Length > 0 ? a[0].ToString() : string.Empty);
        return JSUndefined.Value;
    }

    // -------- Constraint validation --------

    /// <summary>Whether <paramref name="element"/> satisfies constraint validation — a form is valid
    /// when all its controls are; a required input/textarea/select needs a non-empty value.</summary>
    internal bool IsElementValid(DomElement element)
    {
        if (string.Equals(element.TagName, "form", StringComparison.OrdinalIgnoreCase))
            return AreFormChildrenValid(element);

        // A form-associated custom element's validity is whatever it set through its internals; no
        // amount of reading its markup can answer for it.
        if (!_host.IsCustomElementValid(element))
            return false;

        // Individual element validation
        if (!DomBridge.HasAttr(element, "required"))
            return true;

        var tag = element.TagName;
        if (string.Equals(tag, "input", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tag, "textarea", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(tag, "select", StringComparison.OrdinalIgnoreCase))
        {
            DomBridge.TryGetAttribute(element, "value", out var val);
            return !string.IsNullOrEmpty(val);
        }

        return true;
    }

    private bool AreFormChildrenValid(DomElement form)
    {
        foreach (var child in DomBridge.ChildElements(form))
        {
            if (!DomBridge.IsText(child) && !IsElementValid(child))
                return false;
            if (!AreFormChildrenValid(child))
                return false;
        }

        return true;
    }

    /// <summary>The <c>form.elements</c> collection object: a JS array-like with numeric indices and
    /// a <c>length</c>, overriding property lookup so a missing name resolves against the form's
    /// named controls (returning null when unmatched, per HTMLFormControlsCollection).</summary>
    /// <summary>
    /// The control of <paramref name="form"/> whose <c>name</c> is <paramref name="name"/>, or
    /// <see langword="null"/> when none carries it.
    /// </summary>
    /// <remarks>
    /// Shared by <c>form.elements</c>' named access and the form element's own named getter so the
    /// two cannot answer differently. They differ only in what a miss means, which is the callers'
    /// business: the collection reports <c>null</c>, the form leaves the property undefined.
    /// </remarks>
    internal static JSObject? FindNamedControl(DomElement form, string name, IFormHost host)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        foreach (var ctrl in host.CollectFormControls(form))
        {
            if (ctrl.GetAttribute("name") is { } controlName &&
                string.Equals(controlName, name, StringComparison.Ordinal))
                return host.ToJSObject(ctrl);
        }

        return null;
    }

    private sealed class FormElementsCollection(DomElement form, IFormHost host) : JSObject()
    {
        protected override JSValue GetValue(KeyString key, JSValue receiver, bool throwError = true)
        {
            // First check own properties (length, numeric indices, known names)
            var result = base.GetValue(key, receiver, false);
            if (result != null && !result.IsUndefined)
                return result;

            return FindNamedControl(form, key.Value.ToString(), host) is { } named
                ? named
                : JSNull.Value; // HTMLFormControlsCollection returns null for missing names
        }
    }
}

/// <summary>
/// The JS wrapper for a <c>&lt;form&gt;</c>: an ordinary element object that additionally resolves an
/// unknown property name to the control carrying that name — <c>HTMLFormElement</c>'s named getter
/// (HTML §4.10.3).
/// </summary>
/// <remarks>
/// <para>
/// <c>form.elements</c> already offered named access, but the form itself did not, so <c>form.q</c>
/// was <see langword="undefined"/>. That is the spelling pages actually use, and undefined does not
/// announce itself: google.com's homepage hands the result straight to its search-box component —
/// <c>var r=hp_SKb(),u=r.q</c> — which stores it and later reads <c>F.value</c>, throwing
/// <c>Cannot get property value of undefined</c> from a component far from the lookup that failed.
/// </para>
/// <para>
/// Named access is a fallback and not an override: WebIDL consults named properties only when the
/// object and its prototype chain do not already answer, so <c>form.action</c> stays the action
/// attribute even when a control is named <c>action</c>. A name nothing carries is left undefined
/// rather than null, since an absent named property is an absent property.
/// </para>
/// </remarks>
internal sealed class FormElementJSObject(DomElement form, IFormHost host) : JSObject()
{
    protected override JSValue GetValue(KeyString key, JSValue receiver, bool throwError = true)
    {
        var result = base.GetValue(key, receiver, false);
        if (result != null && !result.IsUndefined)
            return result;

        return FormBinding.FindNamedControl(form, key.Value.ToString(), host) is { } named
            ? named
            : base.GetValue(key, receiver, throwError);
    }
}
