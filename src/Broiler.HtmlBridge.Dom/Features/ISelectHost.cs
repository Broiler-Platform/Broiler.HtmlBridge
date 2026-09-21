using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow bridge services the <see cref="SelectBinding"/> feature module needs. The select
/// algorithms (option collection, selected
/// index and value resolution) live in the module, but the per-element form-control state they
/// read/write — the select's dirty selected index, an option's IDL value, an option's
/// default-selected flag — lives in the bridge's <c>FormControlRuntimeState</c> table. It is
/// exposed here as named primitives so the module never touches the runtime-state
/// object, plus the realm and JS-wrapper identity/lookup for the <c>add()</c> and <c>options</c>
/// members.
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine
/// type. The rename is contract-side only: the bridge member behind <see cref="FindElement"/> is
/// still spelled <c>FindDomElementByJSObject</c>, and takes what it takes here.
/// </remarks>
internal interface ISelectHost : INodeWrapperHost, IRealmHost
{
    /// <summary>Resolves the canonical element behind a JS wrapper, or null.</summary>
    DomElement? FindElement(JsValue wrapper);

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
