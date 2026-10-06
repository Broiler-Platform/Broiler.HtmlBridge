using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow bridge services the <see cref="SelectBinding"/> feature module needs. The select
/// algorithms (option collection, selectedness, selected index and value) live in the module, but the
/// per-option state they read and write -- each option's selectedness and dirtiness, which selects hold
/// it, an option's IDL value -- lives in the bridge's tables. It is exposed here as named primitives so
/// the module never touches a table, plus the realm, the JS-wrapper identity and lookup, and the live
/// collections a select hands out.
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

    /// <summary>The option's held selectedness and dirtiness, when it holds them.</summary>
    bool TryGetOptionState(DomElement option, out bool selected, out bool dirty);

    /// <summary>Holds the option's selectedness and dirtiness.</summary>
    void SetOptionState(DomElement option, bool selected, bool dirty);

    /// <summary>Whether the select's options' selectedness is held rather than read from their markup.</summary>
    bool IsSelectHeld(DomElement select);

    /// <summary>Holds the select's options' selectedness from now on.</summary>
    void HoldSelect(DomElement select);

    /// <summary>The select's selection changed: what is styled or serialized from it is stale.</summary>
    void NoteSelectionChanged(DomElement select);

    /// <summary>The option's IDL <c>value</c> (set via the property, not the attribute), if any.</summary>
    bool TryGetOptionValue(DomElement option, out string value);

    /// <summary>
    /// One live <c>HTMLCollection</c> per <paramref name="owner"/> and <paramref name="kind"/>, over
    /// <paramref name="contents"/>: the same object on every read. <paramref name="initialize"/> runs
    /// once, on the collection it makes.
    /// </summary>
    JsValue LiveCollection(DomElement owner, string kind, Func<List<JsValue>> contents, Action<JsValue>? initialize = null);
}
