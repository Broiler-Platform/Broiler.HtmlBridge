using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IFormHost"/>, the narrow contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.FormBinding"/> feature module consumes
/// (HtmlBridge complexity-reduction roadmap Phase 3, P3.9). Explicit interface member, so it does not
/// widen the public <c>DomBridge</c> surface.
/// </summary>
/// <remarks>
/// The form slice is not a seam any more: the module speaks JSEAL, <c>WrapNode</c> is forwarded as it
/// stands and no member converts, so wrapper identity (<c>form.q === form.elements.q</c>) is the same
/// question it was before. (This said the rest of the bridge still held engine objects and
/// <c>JsInterop</c> cast between them.)
/// </remarks>
public sealed partial class DomBridge : IFormHost
{
    IJsRealm IFormHost.Realm => Realm;

    JsValue IFormHost.WrapNode(DomNode node) => WrapNode(node);

    void IFormHost.ResetForm(DomElement form) => ResetFormControls(form);

    IReadOnlyList<DomElement> IFormHost.CollectFormControls(DomElement form) =>
        CollectFormControlsIncludingCustom(form);

    bool IFormHost.IsCustomElementValid(DomElement element) =>
        !CustomElements.IsFormAssociated(element) || ElementInternals.IsValid(element);
}
