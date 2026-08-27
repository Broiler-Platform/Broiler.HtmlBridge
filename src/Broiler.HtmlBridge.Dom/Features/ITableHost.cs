using Broiler.JavaScript.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow bridge services the <see cref="TableBinding"/> feature module needs (HtmlBridge
/// complexity-reduction roadmap Phase 3). The HTMLTable* DOM interface is pure tree manipulation, so
/// it needs only JS-wrapper identity and the bridge's element-construction funnel (which mints a
/// canonical element and registers it for wrapper lookup); every structural operation uses the
/// assembly's neutral static tree helpers on <c>DomBridge</c>.
/// </summary>
internal interface ITableHost
{
    /// <summary>Returns the single JS wrapper identity for <paramref name="node"/>.</summary>
    JSObject ToJSObject(DomNode node);

    /// <summary>Mints a bridge element for <paramref name="tag"/> and registers it so
    /// <see cref="ToJSObject"/> can wrap it (the <c>CreateBridgeElement</c> + known-nodes funnel).</summary>
    DomElement CreateElement(string tag);
}
