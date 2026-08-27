using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The form-control IDL reflectors (HtmlBridge complexity-reduction roadmap Phase 3) — <c>value</c>,
/// <c>checked</c>, <c>type</c>, <c>name</c>, <c>disabled</c>, <c>hidden</c>, <c>tabIndex</c> and
/// <c>required</c>, registered on every element wrapper. <c>value</c>/<c>checked</c> read and write the
/// input's dirty IDL state (and, for <c>&lt;select&gt;</c>, delegate to <see cref="SelectBinding"/>) via
/// the named primitives of the <see cref="IFormControlHost"/> contract; the remaining members are plain
/// content-attribute reflection through the assembly's static <c>DomBridge</c> attribute helpers, with
/// the boolean setters invalidating the style scope (the <c>:disabled</c>/<c>[hidden]</c>/<c>:required</c>
/// selectors depend on it). Was the bridge's <c>JsJsObjectsGetValue106Core</c>..<c>SetRequired121Core</c>
/// callbacks plus their inline registration.
/// </summary>
internal sealed class FormControlBinding(IFormControlHost host)
{
    private readonly IFormControlHost _host = host;

    /// <summary>Installs the form-control IDL reflector members on <paramref name="obj"/> for <paramref name="element"/>.</summary>
    internal void Install(JSObject obj, DomElement element)
    {
        // value (read/write) — for input, textarea, select elements.
        // The IDL 'value' property is NOT reflected as a content attribute for inputs.
        obj.FastAddProperty("value",
            new DomFunction((in _) => GetValue(element), "get value"),
            new DomFunction((in a) => SetValue(element, in a), "set value"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // checked (read/write) — for checkbox and radio inputs. Uses the typed checked-state slot as the
        // "dirty" IDL state that tracks programmatic changes; setAttribute("checked") only sets the
        // content attribute and does NOT affect this IDL state.
        obj.FastAddProperty("checked",
            new DomFunction((in _) => GetChecked(element), "get checked"),
            new DomFunction((in a) => SetChecked(element, in a), "set checked"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // defaultValue (read/write) — the value a reset restores, and the counterpart to the dirty
        // IDL `value` above. It was absent, so `input.defaultValue` read `undefined`: a page
        // comparing the current value against the original to decide whether a field is unsaved
        // compared against `undefined` and concluded "changed" for every field, including the ones
        // it had just reset.
        obj.FastAddProperty("defaultValue",
            new DomFunction((in _) => GetDefaultValue(element), "get defaultValue"),
            new DomFunction((in a) => SetDefaultValue(element, in a), "set defaultValue"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // defaultChecked (read/write) — reflects the `checked` content attribute, which is exactly
        // the state `checked` falls back to when no dirty checkedness has been set.
        obj.FastAddProperty("defaultChecked",
            new DomFunction((in _) => DomBridge.HasAttr(element, "checked") ? JSBoolean.True : JSBoolean.False, "get defaultChecked"),
            new DomFunction((in a) => SetDefaultChecked(element, in a), "set defaultChecked"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // type (read/write) — for input/button elements; getter returns lowercase.
        obj.FastAddProperty("type",
            new DomFunction((in _) => GetType(element), "get type"),
            new DomFunction((in a) => SetType(element, in a), "set type"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // name (read/write) — for form elements; syncs with content attribute.
        obj.FastAddProperty("name",
            new DomFunction((in _) => GetName(element), "get name"),
            new DomFunction((in a) => SetName(element, in a), "set name"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // disabled (read/write) — for form controls.
        obj.FastAddProperty("disabled",
            new DomFunction((in _) => DomBridge.HasAttr(element, "disabled") ? JSBoolean.True : JSBoolean.False, "get disabled"),
            new DomFunction((in a) => SetDisabled(element, in a), "set disabled"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // required (read/write) — form validation.
        obj.FastAddProperty("required",
            new DomFunction((in _) => DomBridge.HasAttr(element, "required") ? JSBoolean.True : JSBoolean.False, "get required"),
            new DomFunction((in a) => SetRequired(element, in a), "set required"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // files (read-only) — a FileList on a file input, null on every other control, exactly as
        // HTML §4.10.5.1.18 has it. It read `undefined` on both, so the standard guard
        // `if (input.files && input.files.length)` was a TypeError on the input it was written for.
        // The list is empty because this engine has no file selection, which is also what a browser
        // reports for an input nobody has touched.
        obj.FastAddProperty("files",
            new DomFunction((in _) => GetFiles(element), "get files"),
            null,
            JSPropertyAttributes.EnumerableConfigurableProperty);
    }

    /// <summary>
    /// The two global reflected attributes that came with this module and are not form-control
    /// members at all: <c>hidden</c> and <c>tabIndex</c> belong to <c>HTMLElement</c> (HTML §3.2.6,
    /// and <c>tabIndex</c> through the <c>HTMLOrSVGElement</c> mixin), so they go on its prototype
    /// while the reflectors above stay per-instance until each control interface has one.
    /// </summary>
    internal void InstallHtmlElementMembers(JSObject target, ElementSource element)
    {
        // hidden (read/write) — global reflected boolean attribute.
        target.FastAddProperty("hidden",
            new DomFunction((in a) => DomBridge.HasAttr(element(in a, "hidden"), "hidden") ? JSBoolean.True : JSBoolean.False, "get hidden"),
            new DomFunction((in a) => SetHidden(element(in a, "hidden"), in a), "set hidden"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // tabIndex (read/write) — global reflected numeric attribute.
        target.FastAddProperty("tabIndex",
            new DomFunction((in a) => GetTabIndex(element(in a, "tabIndex")), "get tabIndex"),
            new DomFunction((in a) => SetTabIndex(element(in a, "tabIndex"), in a), "set tabIndex"),
            JSPropertyAttributes.EnumerableConfigurableProperty);
    }

    private JSValue GetFiles(DomElement element) =>
        string.Equals(element.TagName, "input", StringComparison.OrdinalIgnoreCase) &&
        DomBridge.TryGetAttribute(element, "type", out var inputType) &&
        string.Equals(inputType, "file", StringComparison.OrdinalIgnoreCase)
            ? _host.GetFileList(element)
            : JSNull.Value;

    private JSValue GetValue(DomElement element)
    {
        if (string.Equals(element.TagName, "select", StringComparison.OrdinalIgnoreCase))
            return new JSString(_host.GetSelectValue(element));
        if (_host.TryGetFormControlValue(element, out var sv))
            return new JSString(sv);
        // A textarea has no `value` content attribute: its raw value starts as the child text
        // content (HTML §4.10.11). Falling through to the attribute lookup meant an untouched
        // textarea reported "" no matter what it contained — so a form read before the user typed
        // anything submitted an empty field, and a page pre-filling a textarea through its markup
        // could not read back what it had written.
        if (string.Equals(element.TagName, "textarea", StringComparison.OrdinalIgnoreCase))
            return new JSString(DefaultTextAreaValue(element));
        if (DomBridge.TryGetAttribute(element, "value", out var val))
            return new JSString(val);
        return new JSString(string.Empty);
    }

    /// <summary>
    /// The value a control reverts to on reset: a textarea's child text content, and every other
    /// control's <c>value</c> content attribute.
    /// </summary>
    private static JSValue GetDefaultValue(DomElement element) =>
        string.Equals(element.TagName, "textarea", StringComparison.OrdinalIgnoreCase)
            ? new JSString(DefaultTextAreaValue(element))
            : new JSString(DomBridge.TryGetAttribute(element, "value", out var val) ? val : string.Empty);

    /// <summary>
    /// Writing <c>defaultValue</c> writes the default itself, not the current value — the
    /// <c>value</c> content attribute, or for a textarea its child text, which is where its default
    /// lives. A control with no dirty value flag then reports the new default as its value too,
    /// which is the same coupling <c>setAttribute("value", …)</c> already has.
    /// </summary>
    private JSValue SetDefaultValue(DomElement element, in Arguments a)
    {
        var value = a.Length > 0 ? a[0].ToString() : string.Empty;
        if (string.Equals(element.TagName, "textarea", StringComparison.OrdinalIgnoreCase))
            _host.SetElementTextContent(element, value);
        else
            DomBridge.SetAttr(element, "value", value);
        return JSUndefined.Value;
    }

    /// <summary>Writing <c>defaultChecked</c> sets or removes the <c>checked</c> content
    /// attribute, which is the default it reflects.</summary>
    private JSValue SetDefaultChecked(DomElement element, in Arguments a)
    {
        if (a.Length > 0 && a[0].BooleanValue)
            DomBridge.SetAttr(element, "checked", string.Empty);
        else
            DomBridge.RemoveAttr(element, "checked");
        _host.InvalidateStyleScope(element);
        return JSUndefined.Value;
    }

    /// <summary>A textarea's default value: its child text content (HTML §4.10.11).</summary>
    private static string DefaultTextAreaValue(DomElement element)
    {
        var text = new System.Text.StringBuilder();
        foreach (var child in element.ChildNodes)
        {
            if (DomBridge.IsText(child))
                text.Append(DomBridge.BridgeText(child));
        }

        return text.ToString();
    }

    private JSValue SetValue(DomElement element, in Arguments a)
    {
        var tag = element.TagName.ToLowerInvariant();
        var v = a.Length > 0 ? a[0].ToString() : string.Empty;
        // A textarea sets its dirty value flag exactly as an input does (HTML §4.10.11: "set the
        // element's raw value ... set its dirty value flag to true"), and specifically does NOT
        // touch its children — writing `value` does not rewrite the markup, which is what separates
        // it from writing `defaultValue`. It used to fall through to a `value` content attribute
        // that nothing reads on a textarea; harmless while the getter read that same attribute back,
        // and a lost write once the getter started falling back to the child text the specification
        // names as the default.
        if (tag is "input" or "textarea")
            _host.SetFormControlValue(element, v); // IDL value, not reflected
        else if (tag == "select")
            _host.SetSelectValue(element, v);
        else
            DomBridge.SetAttr(element, "value", v);
        return JSUndefined.Value;
    }

    private JSValue GetChecked(DomElement element)
    {
        // IDL property takes precedence over content attribute
        if (_host.TryGetFormControlChecked(element, out var v))
            return v ? JSBoolean.True : JSBoolean.False;
        return DomBridge.HasAttr(element, "checked") ? JSBoolean.True : JSBoolean.False;
    }

    private JSValue SetChecked(DomElement element, in Arguments a)
    {
        bool newVal = a.Length > 0 && a[0].BooleanValue;
        _host.SetFormControlChecked(element, newVal);
        if (newVal)
        {
            // Radio button mutual exclusion: uncheck others in same group
            if (DomBridge.TryGetAttribute(element, "type", out var tp) && string.Equals(tp, "radio", StringComparison.OrdinalIgnoreCase) && DomBridge.TryGetAttribute(element, "name", out var radioName) && !string.IsNullOrEmpty(radioName))
            {
                // Find the scope for radio group — form parent, or document root if not in a form
                var scope = DomBridge.ParentEl(element);
                while (scope != null && !string.Equals(scope.TagName, "form", StringComparison.OrdinalIgnoreCase))
                    scope = DomBridge.ParentEl(scope);
                if (scope == null)
                {
                    scope = element;
                    while (DomBridge.ParentEl(scope) != null)
                        scope = DomBridge.ParentEl(scope);
                }

                _host.UncheckRadioSiblings(scope, element, radioName);
            }
        }

        return JSUndefined.Value;
    }

    private JSValue GetType(DomElement element)
    {
        if (DomBridge.TryGetAttribute(element, "type", out var t))
            return new JSString(t.ToLowerInvariant());
        // Default type values per HTML spec
        var tag = element.TagName.ToLowerInvariant();
        if (tag == "button")
            return new JSString("submit");
        return new JSString(string.Empty);
    }

    private JSValue SetType(DomElement element, in Arguments a)
    {
        DomBridge.SetAttr(element, "type", a.Length > 0 ? a[0].ToString() : string.Empty);
        return JSUndefined.Value;
    }

    private JSValue GetName(DomElement element)
    {
        if (DomBridge.TryGetAttribute(element, "name", out var n))
            return new JSString(n);
        return new JSString(string.Empty);
    }

    private JSValue SetName(DomElement element, in Arguments a)
    {
        DomBridge.SetAttr(element, "name", a.Length > 0 ? a[0].ToString() : string.Empty);
        return JSUndefined.Value;
    }

    private JSValue SetDisabled(DomElement element, in Arguments a)
    {
        if (a.Length > 0 && a[0].BooleanValue)
            DomBridge.SetAttr(element, "disabled", "disabled");
        else
            DomBridge.RemoveAttr(element, "disabled");
        _host.InvalidateStyleScope(element);
        return JSUndefined.Value;
    }

    private JSValue SetHidden(DomElement element, in Arguments a)
    {
        if (a.Length > 0 && a[0].BooleanValue)
            DomBridge.SetAttr(element, "hidden", string.Empty);
        else
            DomBridge.RemoveAttr(element, "hidden");
        _host.InvalidateStyleScope(element);
        return JSUndefined.Value;
    }

    private JSValue GetTabIndex(DomElement element)
    {
        if (DomBridge.TryGetAttribute(element, "tabindex", out var rawTabIndex) && int.TryParse(rawTabIndex, out var parsedTabIndex))
        {
            return new JSNumber(parsedTabIndex);
        }

        return new JSNumber(-1);
    }

    private JSValue SetTabIndex(DomElement element, in Arguments a)
    {
        if (a.Length == 0)
            return JSUndefined.Value;
        var tabIndex = (int)Math.Truncate(a[0].DoubleValue);
        DomBridge.SetAttr(element, "tabindex", tabIndex.ToString());
        return JSUndefined.Value;
    }

    private JSValue SetRequired(DomElement element, in Arguments a)
    {
        if (a.Length > 0 && a[0].BooleanValue)
            DomBridge.SetAttr(element, "required", "required");
        else
            DomBridge.RemoveAttr(element, "required");
        _host.InvalidateStyleScope(element);
        return JSUndefined.Value;
    }
}
