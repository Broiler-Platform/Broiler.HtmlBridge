using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow bridge services the <see cref="TableBinding"/> feature module needs (HtmlBridge
/// complexity-reduction roadmap Phase 3). The HTMLTable* DOM interface is pure tree manipulation, so
/// it needs only the realm, JS-wrapper identity and the bridge's element-construction funnel (which
/// mints a canonical element and registers it for wrapper lookup); every structural operation uses
/// the assembly's neutral static tree helpers on <c>DomBridge</c>.
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine
/// type. <see cref="WrapNode"/> was <c>ToJSObject</c>: a name that says <em>JSObject</em> is an
/// engine reference too, so it moves with the type it named.
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
