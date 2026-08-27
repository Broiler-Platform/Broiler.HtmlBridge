using Broiler.Dom;

namespace Broiler.HtmlBridge;

// Explicit IGlobalAttributeHost implementation for the GlobalAttributeBinding feature module (Phase 3):
// the only bridge coupling the HTMLElement global attribute reflectors have is style-scope invalidation
// on a selector-affecting write (id/class/dir), forwarded here to the existing internal method.
public sealed partial class DomBridge : Dom.Features.IGlobalAttributeHost
{
    void Dom.Features.IGlobalAttributeHost.InvalidateStyleScope(DomElement element) => InvalidateStyleScope(element);
}
