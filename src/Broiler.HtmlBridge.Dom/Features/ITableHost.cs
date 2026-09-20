using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow bridge services the <see cref="TableBinding"/> feature module needs. The HTMLTable* DOM
/// interface is pure tree manipulation, so
/// it needs only the realm, JS-wrapper identity and the bridge's element-construction funnel (which
/// mints a canonical element and registers it for wrapper lookup); every structural operation uses
/// the assembly's neutral static tree helpers on <c>DomBridge</c>.
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine
/// type — member names included, because a member named after an engine type is a reference to it at
/// every call site that mentions it. The ratchet cannot see that: the engine-reference count in
/// <c>eng/jseal-budget.json</c> matches namespaces as text, not method names, so such a member
/// measures zero either way. These seams are named for what they do because of what a reader takes
/// from the name, which is the whole of the argument for it.
/// </remarks>
internal interface ITableHost
{
    /// <summary>The realm the row/cell collections and the installed members are built in.</summary>
    IJsRealm Realm { get; }

    /// <summary>Returns the single JS wrapper identity for <paramref name="node"/>.</summary>
    JsValue WrapNode(DomNode node);

    /// <summary>Mints a bridge element for <paramref name="tag"/> and registers it so
    /// <see cref="WrapNode"/> can wrap it (the <c>CreateBridgeElement</c> + known-nodes funnel).</summary>
    DomElement CreateElement(string tag);
}
