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
/// The table slice is spelled in JSEAL end to end: every member of this contract is realm- or
/// handle-typed, and the module's last engine-typed member went with the installer overload whose
/// caller had stopped needing it. What stood here called this "the half-migrated seam for the table
/// slice" and named <see cref="Dom.Runtime.JsInterop"/> as "the cast between them": this file has
/// never performed that cast, and since the installer's deletion nothing in the slice does. The
/// property the remark was defending holds and still matters -- a JSEAL object handle carries the
/// engine's own object, so wrapper identity (<c>row === row</c>, and the weak tables keyed on it) is
/// the same question it was before.
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
