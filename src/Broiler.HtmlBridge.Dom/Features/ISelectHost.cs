using Broiler.JavaScript.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow bridge services the <see cref="SelectBinding"/> feature module needs (HtmlBridge
/// complexity-reduction roadmap Phase 3, P3.8). The select algorithms (option collection, selected
/// index and value resolution) move into the module, but the per-element form-control state they
/// read/write — the select's dirty selected index, an option's IDL value, an option's
/// default-selected flag — lives on the bridge's <c>ElementRuntimeState.FormControl</c>. It is
/// exposed here as named primitives (the P3.7 pattern) so the module never touches the runtime-state
/// object, plus JS-wrapper identity/lookup for the <c>add()</c> and <c>options</c> members.
/// </summary>
internal interface ISelectHost
{
    /// <summary>Returns the single JS wrapper identity for <paramref name="node"/>.</summary>
    JSObject ToJSObject(DomNode node);

    /// <summary>Resolves the canonical element behind a JS wrapper, or null.</summary>
    DomElement? FindDomElementByJSObject(JSObject? jsObj);

    /// <summary>The select's explicitly-set ("dirty") selected index, if any.</summary>
    bool TryGetSelectedIndex(DomElement select, out int index);

    /// <summary>Sets the select's dirty selected index.</summary>
    void SetSelectedIndex(DomElement select, int index);

    /// <summary>The option's IDL <c>value</c> (set via the property, not the attribute), if any.</summary>
    bool TryGetOptionValue(DomElement option, out string value);

    /// <summary>Whether the option's <c>defaultSelected</c> flag is set.</summary>
    bool GetOptionDefaultSelected(DomElement option);

    /// <summary>Sets the option's <c>defaultSelected</c> flag.</summary>
    void SetOptionDefaultSelected(DomElement option, bool value);
}
