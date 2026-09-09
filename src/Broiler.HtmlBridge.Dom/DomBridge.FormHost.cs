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
/// This is the half-migrated seam for the form slice: the module speaks JSEAL, the rest of the bridge
/// still holds engine objects, and <see cref="Dom.Runtime.JsInterop"/> is the cast between them. It
/// is a cast and not a conversion — a JSEAL object handle carries the engine's own object — so
/// wrapper identity (<c>form.q === form.elements.q</c>, and the weak tables keyed on it) is the same
/// question it was before.
/// </remarks>
public sealed partial class DomBridge : IFormHost
{
    IJsRealm IFormHost.Realm => Realm;

    JsValue IFormHost.WrapNode(DomNode node) => JsInterop.FromEngineObject(ToJSObject(node));

    void IFormHost.ResetForm(DomElement form) => ResetFormControls(form);

    IReadOnlyList<DomElement> IFormHost.CollectFormControls(DomElement form) =>
        CollectFormControlsIncludingCustom(form);

    bool IFormHost.IsCustomElementValid(DomElement element) =>
        !CustomElements.IsFormAssociated(element) || ElementInternals.IsValid(element);
}
