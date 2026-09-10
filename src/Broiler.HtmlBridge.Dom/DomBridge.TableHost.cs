using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="ITableHost"/>, the narrow contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.TableBinding"/> feature module consumes
/// (HtmlBridge complexity-reduction roadmap Phase 3, P3.5). Explicit interface members, so these
/// seams do not widen the public <c>DomBridge</c> surface.
/// </summary>
/// <remarks>
/// This is the half-migrated seam for the table slice: the module speaks JSEAL, the rest of the
/// bridge still holds engine objects, and <see cref="Dom.Runtime.JsInterop"/> is the cast between
/// them. It is a cast and not a conversion — a JSEAL object handle carries the engine's own object —
/// so wrapper identity (<c>row === row</c>, and the weak tables keyed on it) is the same question it
/// was before.
/// </remarks>
public sealed partial class DomBridge : ITableHost
{
    IJsRealm ITableHost.Realm => Realm;

    JsValue ITableHost.WrapNode(DomNode node) => WrapNode(node);

    DomElement ITableHost.CreateElement(string tag)
    {
        var element = CreateBridgeElement(tag);
        return element;
    }
}
