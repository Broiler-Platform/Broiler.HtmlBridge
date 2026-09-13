using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow bridge services the <see cref="SelectBinding"/> feature module needs (HtmlBridge
/// complexity-reduction roadmap Phase 3, P3.8). The select algorithms (option collection, selected
/// index and value resolution) move into the module, but the per-element form-control state they
/// read/write — the select's dirty selected index, an option's IDL value, an option's
/// default-selected flag — lives on the bridge's <c>ElementRuntimeState.FormControl</c>. It is
/// exposed here as named primitives (the P3.7 pattern) so the module never touches the runtime-state
/// object, plus the realm and JS-wrapper identity/lookup for the <c>add()</c> and <c>options</c>
/// members.
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine
/// type. <see cref="WrapNode"/> and <see cref="FindElement"/> were <c>ToJSObject</c> and
/// <c>FindDomElementByJSObject</c>, and this used to call the second of those "an engine reference
/// too". It is not one the ratchet can see: <c>eng/jseal-budget.json</c> counts the engine's namespace
/// as text, and a method name spells no namespace — the bridge-side member still carries the old name,
/// still takes what it now takes, and measures zero either way. The renames were worth making for what
/// a reader takes from them, which is the honest argument and a different one.
/// </remarks>
internal interface ISelectHost
{
    /// <summary>
    /// The realm the select's <c>options</c> array and its members are built in, and which the
    /// installed members' bodies run against.
    /// </summary>
    IJsRealm Realm { get; }

    /// <summary>Returns the single JS wrapper identity for <paramref name="node"/>.</summary>
    JsValue WrapNode(DomNode node);

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
