using Broiler.JavaScript.Runtime;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IFormHost"/>, the narrow contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.FormBinding"/> feature module consumes
/// (HtmlBridge complexity-reduction roadmap Phase 3, P3.9). Explicit interface member, so it does not
/// widen the public <c>DomBridge</c> surface.
/// </summary>
public sealed partial class DomBridge : IFormHost
{
    JSObject IFormHost.ToJSObject(DomNode node) => ToJSObject(node);

    void IFormHost.ResetForm(DomElement form) => ResetFormControls(form);

    IReadOnlyList<DomElement> IFormHost.CollectFormControls(DomElement form) =>
        CollectFormControlsIncludingCustom(form);

    bool IFormHost.IsCustomElementValid(DomElement element) =>
        !CustomElements.IsFormAssociated(element) || ElementInternals.IsValid(element);
}
