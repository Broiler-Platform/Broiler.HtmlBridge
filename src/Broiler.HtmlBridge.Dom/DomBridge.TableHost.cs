using Broiler.JavaScript.Runtime;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="ITableHost"/>, the narrow contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.TableBinding"/> feature module consumes
/// (HtmlBridge complexity-reduction roadmap Phase 3, P3.5). Explicit interface members, so these
/// seams do not widen the public <c>DomBridge</c> surface.
/// </summary>
public sealed partial class DomBridge : ITableHost
{
    JSObject ITableHost.ToJSObject(DomNode node) => ToJSObject(node);

    DomElement ITableHost.CreateElement(string tag)
    {
        var element = CreateBridgeElement(tag);
        return element;
    }
}
