using Broiler.HtmlBridge.Jseal;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow bridge services the <see cref="AttributesBinding"/> feature module needs (HtmlBridge
/// complexity-reduction roadmap Phase 3, P3.12). The attribute write path
/// (<c>setAttribute</c>/<c>removeAttribute</c> and their <c>NS</c> variants) coordinates several
/// subsystems beyond the canonical attribute set: it re-applies the <c>style</c> attribute to the
/// element's inline style, compiles an <c>on*</c> inline event handler, invalidates the style scope,
/// and queues an attribute mutation record. Those cross-cutting side effects stay owned by the bridge
/// (CSSOM inline style, the Events inline-handler compiler, the CSS invalidation route and the
/// MutationObserver hub); the module reaches each through a named seam here, implemented explicitly on
/// <see cref="DomBridge"/> so the public surface is unchanged.
/// </summary>
internal interface IAttributesHost
{
    /// <summary>
    /// The realm the <c>Attr</c> wrappers are built in. It is what the module's migrated half —
    /// the <c>Attr</c> object model — creates and reads members through.
    /// </summary>
    IJsRealm Realm { get; }

    /// <summary>
    /// The bridge's JS context, and the contract's one remaining engine reference.
    /// </summary>
    /// <remarks>
    /// One consumer keeps it: the <c>InvalidCharacterError</c> an invalid <c>setAttribute</c>,
    /// <c>setAttributeNS</c> or <c>toggleAttribute</c> name must throw (DOM §4.9.1). The bridge's name
    /// validators — <c>DomBridge/Utilities.NameValidation.cs</c>, which this group does not own — mint
    /// that <c>DOMException</c> from a context rather than from a realm, so a caller has to hold one.
    /// It goes when they take an <see cref="IJsRealm"/> and reach <see cref="IJsCalls.DomError"/>
    /// instead. The <see cref="DomCollectionBinding"/> half that also needed it has migrated.
    /// </remarks>
    Broiler.JavaScript.Engine.JSContext? JsContext { get; }

    /// <summary>Applies a <c>style</c> attribute value to the element's inline style declaration
    /// (clearing and reparsing it) and invalidates the element's style scope.</summary>
    void ApplyStyleAttribute(DomElement element, string value);

    /// <summary>Compiles an <c>on*</c> inline event-handler attribute into a listener on the element.</summary>
    void CompileInlineEventAttribute(DomElement element, string attributeName, string code);

    /// <summary>Invalidates the element's style scope so an attribute change re-cascades.</summary>
    void InvalidateStyleScope(DomElement element);

    /// <summary>Queues an <c>attributes</c> MutationObserver record for the change.</summary>
    void NotifyAttributeMutationObservers(DomElement element, string attributeName, string? oldValue);

    /// <summary>Points a wrapper at a named interface's prototype. Needed here because an attribute
    /// is not a <c>DomNode</c>, so its wrapper is not minted at the choke point that links every
    /// other one.</summary>
    void LinkToInterface(JsValue wrapper, string interfaceName);
}
