using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

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
/// <remarks>
/// <para>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>) throughout: every member is minted by
/// the realm and every body runs on a <see cref="JsCall"/>, so this file names no engine type.
/// </para>
/// <para>
/// <b>The two <c>HTMLElement</c> members were the last thing here that did not.</b> <c>hidden</c> and
/// <c>tabIndex</c> go on <c>HTMLElement.prototype</c>, so they serve every element and have to resolve
/// the receiver on each call — through a delegate whose parameter used to be the engine's own argument
/// frame, which is what kept <see cref="InstallHtmlElementMembers"/> engine-typed. That delegate is
/// one JSEAL declaration now (<see cref="JsElementSource"/>) and
/// <c>DomBridge/HtmlElementInterface.cs</c> installs against it, so the pair is minted by the realm
/// with the same attributes and in the same position. Each body still does nothing but read its
/// argument and call the shared CLR-typed operation, so the two spellings cannot drift apart.
/// </para>
/// <para>
/// The <c>tabIndex</c> setter's coercion moved with the frame and is the same ECMAScript operation on
/// the same value: the engine's <c>DoubleValue</c> is <c>ToNumber</c>, and so is
/// <see cref="IJsRealm.ToNumber"/> — which matters, because <c>el.tabIndex = "3"</c> is a string a
/// page really does assign.
/// </para>
/// </remarks>
internal sealed class FormControlBinding(IFormControlHost host)
{
    private readonly IFormControlHost _host = host;

    /// <summary>Installs the form-control IDL reflector members on <paramref name="obj"/> for <paramref name="element"/>.</summary>
    internal void Install(JsValue obj, DomElement element)
    {
        var realm = _host.Realm;

        // value (read/write) — for input, textarea, select elements.
        // The IDL 'value' property is NOT reflected as a content attribute for inputs.
        realm.DefineAccessor(obj, "value",
            (in _) => JsValue.String(GetValue(element)),
            (in call) => SetValue(element, in call));

        // checked (read/write) — for checkbox and radio inputs. Uses the typed checked-state slot as the
        // "dirty" IDL state that tracks programmatic changes; setAttribute("checked") only sets the
        // content attribute and does NOT affect this IDL state.
        realm.DefineAccessor(obj, "checked",
            (in _) => JsValue.Boolean(GetChecked(element)),
            (in call) => SetChecked(element, in call));

        // defaultValue (read/write) — the value a reset restores, and the counterpart to the dirty
        // IDL `value` above. It was absent, so `input.defaultValue` read `undefined`: a page
        // comparing the current value against the original to decide whether a field is unsaved
        // compared against `undefined` and concluded "changed" for every field, including the ones
        // it had just reset.
        realm.DefineAccessor(obj, "defaultValue",
            (in _) => JsValue.String(GetDefaultValue(element)),
            (in call) => SetDefaultValue(element, in call));

        // defaultChecked (read/write) — reflects the `checked` content attribute, which is exactly
        // the state `checked` falls back to when no dirty checkedness has been set.
        realm.DefineAccessor(obj, "defaultChecked",
            (in _) => JsValue.Boolean(DomBridge.HasAttr(element, "checked")),
            (in call) => SetDefaultChecked(element, in call));

        // type (read/write) — for input/button elements; getter returns lowercase.
        realm.DefineAccessor(obj, "type",
            (in _) => JsValue.String(GetType(element)),
            (in call) => SetType(element, in call));

        // name (read/write) — for form elements; syncs with content attribute.
        realm.DefineAccessor(obj, "name",
            (in _) => JsValue.String(GetName(element)),
            (in call) => SetName(element, in call));

        // disabled (read/write) — for form controls.
        realm.DefineAccessor(obj, "disabled",
            (in _) => JsValue.Boolean(DomBridge.HasAttr(element, "disabled")),
            (in call) => SetDisabled(element, in call));

        // required (read/write) — form validation.
        realm.DefineAccessor(obj, "required",
            (in _) => JsValue.Boolean(DomBridge.HasAttr(element, "required")),
            (in call) => SetRequired(element, in call));

        // files (read-only) — a FileList on a file input, null on every other control, exactly as
        // HTML §4.10.5.1.18 has it. It read `undefined` on both, so the standard guard
        // `if (input.files && input.files.length)` was a TypeError on the input it was written for.
        // The list is empty because this engine has no file selection, which is also what a browser
        // reports for an input nobody has touched.
        realm.DefineAccessor(obj, "files", (in _) => GetFiles(element), null);
    }

    /// <summary>
    /// The two global reflected attributes that came with this module and are not form-control
    /// members at all: <c>hidden</c> and <c>tabIndex</c> belong to <c>HTMLElement</c> (HTML §3.2.6,
    /// and <c>tabIndex</c> through the <c>HTMLOrSVGElement</c> mixin), so they go on its prototype
    /// while the reflectors above stay per-instance until each control interface has one.
    /// </summary>
    /// <remarks>
    /// Both bodies do nothing but read their argument and call the shared operation; see the remarks
    /// on this class for the coercion that read performs.
    /// </remarks>
    internal void InstallHtmlElementMembers(JsValue target, JsElementSource element)
    {
        var realm = _host.Realm;

        // hidden (read/write) — global reflected boolean attribute.
        realm.DefineAccessor(target, "hidden",
            (in call) => JsValue.Boolean(DomBridge.HasAttr(element(in call, "hidden"), "hidden")),
            (in call) =>
            {
                SetHidden(element(in call, "hidden"), call.Length > 0 && call[0].AsBoolean);
                return JsValue.Undefined;
            });

        // tabIndex (read/write) — global reflected numeric attribute.
        realm.DefineAccessor(target, "tabIndex",
            (in call) => JsValue.Number(GetTabIndex(element(in call, "tabIndex"))),
            (in call) =>
            {
                if (call.Length > 0)
                    SetTabIndex(element(in call, "tabIndex"), call.Realm.ToNumber(call[0]));
                return JsValue.Undefined;
            });
    }

    private JsValue GetFiles(DomElement element) =>
        string.Equals(element.TagName, "input", StringComparison.OrdinalIgnoreCase) &&
        DomBridge.TryGetAttribute(element, "type", out var inputType) &&
        string.Equals(inputType, "file", StringComparison.OrdinalIgnoreCase)
            ? _host.GetFileList(element)
            : JsValue.Null;

    private string GetValue(DomElement element)
    {
        if (string.Equals(element.TagName, "select", StringComparison.OrdinalIgnoreCase))
            return _host.GetSelectValue(element);
        if (_host.TryGetFormControlValue(element, out var sv))
            return sv;
        // A textarea has no `value` content attribute: its raw value starts as the child text
        // content (HTML §4.10.11). Falling through to the attribute lookup meant an untouched
        // textarea reported "" no matter what it contained — so a form read before the user typed
        // anything submitted an empty field, and a page pre-filling a textarea through its markup
        // could not read back what it had written.
        if (string.Equals(element.TagName, "textarea", StringComparison.OrdinalIgnoreCase))
            return DefaultTextAreaValue(element);
        if (DomBridge.TryGetAttribute(element, "value", out var val))
            return val;
        return string.Empty;
    }

    /// <summary>
    /// The value a control reverts to on reset: a textarea's child text content, and every other
    /// control's <c>value</c> content attribute.
    /// </summary>
    private static string GetDefaultValue(DomElement element) =>
        string.Equals(element.TagName, "textarea", StringComparison.OrdinalIgnoreCase)
            ? DefaultTextAreaValue(element)
            : DomBridge.TryGetAttribute(element, "value", out var val) ? val : string.Empty;

    /// <summary>
    /// Writing <c>defaultValue</c> writes the default itself, not the current value — the
    /// <c>value</c> content attribute, or for a textarea its child text, which is where its default
    /// lives. A control with no dirty value flag then reports the new default as its value too,
    /// which is the same coupling <c>setAttribute("value", …)</c> already has.
    /// </summary>
    private JsValue SetDefaultValue(DomElement element, in JsCall call)
    {
        var value = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
        if (string.Equals(element.TagName, "textarea", StringComparison.OrdinalIgnoreCase))
            _host.SetElementTextContent(element, value);
        else
            DomBridge.SetAttr(element, "value", value);
        return JsValue.Undefined;
    }

    /// <summary>Writing <c>defaultChecked</c> sets or removes the <c>checked</c> content
    /// attribute, which is the default it reflects.</summary>
    private JsValue SetDefaultChecked(DomElement element, in JsCall call)
    {
        if (call[0].AsBoolean)
            DomBridge.SetAttr(element, "checked", string.Empty);
        else
            DomBridge.RemoveAttr(element, "checked");
        _host.InvalidateStyleScope(element);
        return JsValue.Undefined;
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

    private JsValue SetValue(DomElement element, in JsCall call)
    {
        var tag = element.TagName.ToLowerInvariant();
        // ToJsString, not the handle's rendering: `input.value = obj` runs the object's own
        // toString, which is the coercion a page observes when it reads the value back.
        var v = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
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
        return JsValue.Undefined;
    }

    private bool GetChecked(DomElement element)
    {
        // IDL property takes precedence over content attribute
        if (_host.TryGetFormControlChecked(element, out var v))
            return v;
        return DomBridge.HasAttr(element, "checked");
    }

    private JsValue SetChecked(DomElement element, in JsCall call)
    {
        bool newVal = call[0].AsBoolean;
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

        return JsValue.Undefined;
    }

    private static string GetType(DomElement element)
    {
        if (DomBridge.TryGetAttribute(element, "type", out var t))
            return t.ToLowerInvariant();
        // Default type values per HTML spec
        var tag = element.TagName.ToLowerInvariant();
        if (tag == "button")
            return "submit";
        return string.Empty;
    }

    private static JsValue SetType(DomElement element, in JsCall call)
    {
        DomBridge.SetAttr(element, "type", call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty);
        return JsValue.Undefined;
    }

    private static string GetName(DomElement element)
    {
        if (DomBridge.TryGetAttribute(element, "name", out var n))
            return n;
        return string.Empty;
    }

    private static JsValue SetName(DomElement element, in JsCall call)
    {
        DomBridge.SetAttr(element, "name", call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty);
        return JsValue.Undefined;
    }

    private JsValue SetDisabled(DomElement element, in JsCall call)
    {
        if (call[0].AsBoolean)
            DomBridge.SetAttr(element, "disabled", "disabled");
        else
            DomBridge.RemoveAttr(element, "disabled");
        _host.InvalidateStyleScope(element);
        return JsValue.Undefined;
    }

    /// <summary>
    /// <c>hidden</c>'s write half, CLR-typed so that the prototype installer above holds nothing but
    /// the argument read — which is what let that installer migrate without this being rewritten.
    /// </summary>
    private void SetHidden(DomElement element, bool hidden)
    {
        if (hidden)
            DomBridge.SetAttr(element, "hidden", string.Empty);
        else
            DomBridge.RemoveAttr(element, "hidden");
        _host.InvalidateStyleScope(element);
    }

    private static int GetTabIndex(DomElement element)
    {
        if (DomBridge.TryGetAttribute(element, "tabindex", out var rawTabIndex) && int.TryParse(rawTabIndex, out var parsedTabIndex))
        {
            return parsedTabIndex;
        }

        return -1;
    }

    /// <summary>
    /// <c>tabIndex</c>'s write half. It takes the already-coerced number rather than the argument, so
    /// that the coercion stays where the argument is — the realm's <c>ToNumber</c> now, the engine's
    /// <c>DoubleValue</c> before the installer migrated; both are the same ECMAScript operation.
    /// </summary>
    private static void SetTabIndex(DomElement element, double tabIndex) =>
        DomBridge.SetAttr(element, "tabindex", ((int)Math.Truncate(tabIndex)).ToString());

    private JsValue SetRequired(DomElement element, in JsCall call)
    {
        if (call[0].AsBoolean)
            DomBridge.SetAttr(element, "required", "required");
        else
            DomBridge.RemoveAttr(element, "required");
        _host.InvalidateStyleScope(element);
        return JsValue.Undefined;
    }
}
