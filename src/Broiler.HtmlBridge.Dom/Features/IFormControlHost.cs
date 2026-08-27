using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow bridge services the <see cref="FormControlBinding"/> feature module needs (HtmlBridge
/// complexity-reduction roadmap Phase 3). The form-control IDL reflectors move into the module, but the
/// per-element form-control state they read/write — the input's dirty IDL <c>value</c> and <c>checked</c>
/// state — lives on the bridge's <c>ElementRuntimeState.FormControl</c>. It is exposed here as named
/// primitives (the P3.7 pattern) so the module never touches the runtime-state object, together with the
/// <c>&lt;select&gt;</c> value resolution (owned by <see cref="SelectBinding"/>), the radio-group
/// mutual-exclusion walk, and style-scope invalidation for the reflected boolean setters. Neutral
/// attribute reflection uses the assembly's static <c>DomBridge</c> helpers directly.
/// </summary>
internal interface IFormControlHost
{
    /// <summary>
    /// The file input's <c>FileList</c> — one object per element, so <c>input.files ===
    /// input.files</c> holds as it does in a browser. Always empty: there is no file selection.
    /// </summary>
    JSValue GetFileList(DomElement element);

    /// <summary>The input's dirty IDL <c>value</c> (set via the property, not the attribute), if any.</summary>
    bool TryGetFormControlValue(DomElement element, out string value);

    /// <summary>Sets the input's dirty IDL <c>value</c>.</summary>
    void SetFormControlValue(DomElement element, string value);

    /// <summary>Resolves the <c>&lt;select&gt;</c> element's current value (delegates to SelectBinding).</summary>
    string GetSelectValue(DomElement element);

    /// <summary>Sets the <c>&lt;select&gt;</c> element's value (delegates to SelectBinding).</summary>
    void SetSelectValue(DomElement element, string value);

    /// <summary>The input's dirty IDL <c>checked</c> state, if any.</summary>
    bool TryGetFormControlChecked(DomElement element, out bool value);

    /// <summary>Sets the input's dirty IDL <c>checked</c> state.</summary>
    void SetFormControlChecked(DomElement element, bool value);

    /// <summary>Unchecks the other radio inputs sharing <paramref name="radioName"/> within <paramref name="scope"/>.</summary>
    void UncheckRadioSiblings(DomElement scope, DomElement except, string radioName);

    /// <summary>
    /// Replaces <paramref name="element"/>'s children with a single text node holding
    /// <paramref name="value"/>. Needed for one member: a <c>&lt;textarea&gt;</c>'s
    /// <c>defaultValue</c> <em>is</em> its child text content, so writing the default is a tree
    /// mutation rather than an attribute write.
    /// </summary>
    void SetElementTextContent(DomElement element, string value);

    /// <summary>Invalidates the cascade/computed style scope anchored at <paramref name="anchor"/>.</summary>
    void InvalidateStyleScope(DomElement anchor);
}
