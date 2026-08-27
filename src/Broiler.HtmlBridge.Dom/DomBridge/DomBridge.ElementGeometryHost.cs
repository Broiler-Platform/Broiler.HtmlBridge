using Broiler.JavaScript.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

// Explicit IElementGeometryHost implementation for the ElementGeometryBinding feature module (Phase 3):
// the box-model metrics and scrolling operations are the one Phase 3 family that genuinely reads the live
// layout, so the contract is wide by design. Each member forwards to the existing private LayoutMetrics.*
// method — the module now names the exact geometry surface it depends on instead of reaching into the
// bridge directly.
public sealed partial class DomBridge : Dom.Features.IElementGeometryHost
{
    bool Dom.Features.IElementGeometryHost.IsViewportElementForMetrics(DomElement element) => IsViewportElementForMetrics(element);

    double Dom.Features.IElementGeometryHost.GetClientTopForDomElement(DomElement element) => GetClientTopForDomElement(element);
    double Dom.Features.IElementGeometryHost.GetClientLeftForDomElement(DomElement element) => GetClientLeftForDomElement(element);
    double Dom.Features.IElementGeometryHost.GetClientWidthForDomElement(DomElement element, bool isRoot) => GetClientWidthForDomElement(element, isRoot);
    double Dom.Features.IElementGeometryHost.GetClientHeightForDomElement(DomElement element, bool isRoot) => GetClientHeightForDomElement(element, isRoot);
    double Dom.Features.IElementGeometryHost.GetOffsetWidthForDomElement(DomElement element, bool isRoot) => GetOffsetWidthForDomElement(element, isRoot);
    double Dom.Features.IElementGeometryHost.GetOffsetHeightForDomElement(DomElement element, bool isRoot) => GetOffsetHeightForDomElement(element, isRoot);
    double Dom.Features.IElementGeometryHost.GetScrollWidthForDomElement(DomElement element, bool isRoot) => GetScrollWidthForDomElement(element, isRoot);
    double Dom.Features.IElementGeometryHost.GetScrollHeightForDomElement(DomElement element, bool isRoot) => GetScrollHeightForDomElement(element, isRoot);
    double Dom.Features.IElementGeometryHost.GetOffsetTopForDomElement(DomElement element) => GetOffsetTopForDomElement(element);
    double Dom.Features.IElementGeometryHost.GetOffsetLeftForDomElement(DomElement element) => GetOffsetLeftForDomElement(element);

    double? Dom.Features.IElementGeometryHost.GetElementScrollOffset(DomElement element, bool vertical) => GetElementScrollOffset(element, vertical);

    void Dom.Features.IElementGeometryHost.SetElementScrollOffsetsWithBehavior(DomElement element,
        double? left, double? top, bool relative, bool clamp, string? behavior)
        => SetElementScrollOffsetsWithBehavior(element, left, top, relative, clamp, behavior);

    DomElement? Dom.Features.IElementGeometryHost.GetOffsetParentForDomElement(DomElement element) => GetOffsetParentForDomElement(element);
    DomElement? Dom.Features.IElementGeometryHost.GetScrollParentForDomElement(DomElement element) => GetScrollParentForDomElement(element);

    (double Left, double Top, double Width, double Height) Dom.Features.IElementGeometryHost.GetBoundingClientRectForDomElement(DomElement element, bool isRoot)
        => GetBoundingClientRectForDomElement(element, isRoot);

    (string Block, string Inline, string? Behavior) Dom.Features.IElementGeometryHost.GetScrollIntoViewOptions(in Arguments args) => GetScrollIntoViewOptions(args);

    void Dom.Features.IElementGeometryHost.ScrollElementIntoView(DomElement element, string? block, string? inline, string? behavior)
        => ScrollElementIntoView(element, block, inline, behavior);

    (double? Left, double? Top, string? Behavior) Dom.Features.IElementGeometryHost.GetScrollArguments(in Arguments args) => GetScrollArguments(args);

    JSObject Dom.Features.IElementGeometryHost.ToJSObject(DomNode node) => ToJSObject(node);
}
