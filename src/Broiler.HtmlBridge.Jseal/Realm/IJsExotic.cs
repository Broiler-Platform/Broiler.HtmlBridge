namespace Broiler.HtmlBridge.Jseal;

/// <summary>
/// Host-defined property lookup, for the six DOM objects whose members are not a fixed list: live
/// collections (indexed and named), a form's controls, <c>CSSStyleDeclaration</c>'s dashed properties,
/// and <c>Storage</c>'s keys.
/// </summary>
/// <remarks>
/// <para>
/// These are the objects the bridge expresses today by subclassing <c>JSObject</c> and overriding its
/// property-lookup members — the deepest engine coupling in the whole binding layer, because it
/// depends not only on the engine's types but on its lookup <em>protocol</em>. Six classes do it.
/// Declaring the hook rather than inheriting it is what lets an engine that dispatches lookups
/// differently — through a Proxy, through a C callback table — serve the same DOM object.
/// </para>
/// <para>
/// <b>Ordinary properties win, and the order is not negotiable.</b> The engine consults its own
/// property storage first and only asks a handler when it finds nothing. This is what WebIDL's
/// named-property semantics require and what the existing subclasses do — each calls the base lookup
/// before its own — and getting it backwards is silently wrong rather than loudly wrong: a collection
/// that happens to contain an element named <c>item</c> would start shadowing its own
/// <c>item()</c> method, and every ordinary member of a style declaration would be interceptable by a
/// CSS property of the same name.
/// </para>
/// </remarks>
public interface IJsExotic
{
    /// <summary>
    /// Answers a named lookup the object's ordinary properties did not, or reports that there is no
    /// such property.
    /// </summary>
    bool TryGetNamed(string name, out JsValue value);

    /// <summary>
    /// Answers an integer-indexed lookup the object's ordinary properties did not.
    /// </summary>
    bool TryGetIndex(uint index, out JsValue value);

    /// <summary>
    /// Handles an assignment to a named property, or declines it so that the ordinary assignment
    /// happens.
    /// </summary>
    bool TrySetNamed(string name, JsValue value);

    /// <summary>
    /// The names this object supplies beyond its ordinary properties, for <c>Object.keys</c>,
    /// <c>for…in</c> and spread. Ordinary properties are added by the engine and must not be repeated
    /// here.
    /// </summary>
    IReadOnlyList<string> SupportedNames { get; }

    /// <summary>
    /// How many integer-indexed elements this object currently has — its <c>length</c> for
    /// enumeration purposes.
    /// </summary>
    /// <remarks>
    /// Asked immediately before an enumeration, so a live collection reports what it holds now rather
    /// than what it held when it was minted. The Broiler.JS provider uses this to materialise the
    /// index properties its engine's enumeration requires, which is a fact about that engine's
    /// property storage and stays inside the provider.
    /// </remarks>
    uint IndexedLength { get; }
}
