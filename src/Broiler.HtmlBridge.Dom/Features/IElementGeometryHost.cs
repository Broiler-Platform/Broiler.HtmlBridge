using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The host surface <see cref="ElementGeometryBinding"/> needs from the bridge: the CSSOM/HTML box-model
/// metrics (<c>client*</c>/<c>offset*</c>/<c>scroll*</c> dimensions, <c>offsetTop</c>/<c>offsetLeft</c>,
/// <c>offsetParent</c>, <c>getBoundingClientRect</c>/<c>getClientRects</c>) and the imperative scrolling
/// operations (<c>scrollTop</c>/<c>scrollLeft</c> get/set, <c>scroll</c>/<c>scrollTo</c>/<c>scrollBy</c>,
/// <c>scrollIntoView</c>, <c>scrollParent</c>). These are the one Phase 3 family that genuinely reads the
/// live layout — every value comes from the bridge's layout cache — so unlike the stateless feature modules
/// this contract is deliberately wide: it names the exact geometry surface the module depends on, so the
/// callbacks no longer reach into arbitrary bridge internals (the Phase 3 "wide-explicit-host" template).
/// The bridge implements it explicitly in <c>DomBridge.ElementGeometryHost.cs</c>, forwarding to the
/// existing private <c>LayoutMetrics.*</c> methods.
/// </summary>
/// <remarks>
/// <para>
/// The contract names no engine type — including in its member names, which are references to one just
/// as a parameter type is. <see cref="WrapNode"/> and <see cref="GetScrollOptions"/> were named after
/// the engine's object type and its call frame respectively, so both moved when those types did.
/// </para>
/// <para>
/// <b>The two dictionary readers still take the whole call, and that is deliberate.</b>
/// <see cref="GetScrollIntoViewOptions"/> and <see cref="GetScrollOptions"/> answer from the shape of
/// what was passed — nothing, a boolean, a scroll-options dictionary, or a pair of coordinates — so the
/// arity and the kind are the input, not a value the module could read out first. Both are shared word
/// for word with the window and sub-window scroll surfaces, which is why they stay the bridge's single
/// implementation rather than being re-derived per element.
/// </para>
/// </remarks>
internal interface IElementGeometryHost
{
    bool IsViewportElementForMetrics(DomElement element);

    double GetClientTopForDomElement(DomElement element);
    double GetClientLeftForDomElement(DomElement element);
    double GetClientWidthForDomElement(DomElement element, bool isRoot);
    double GetClientHeightForDomElement(DomElement element, bool isRoot);
    double GetOffsetWidthForDomElement(DomElement element, bool isRoot);
    double GetOffsetHeightForDomElement(DomElement element, bool isRoot);
    double GetScrollWidthForDomElement(DomElement element, bool isRoot);
    double GetScrollHeightForDomElement(DomElement element, bool isRoot);
    double GetOffsetTopForDomElement(DomElement element);
    double GetOffsetLeftForDomElement(DomElement element);

    double? GetElementScrollOffset(DomElement element, bool vertical);
    void SetElementScrollOffsetsWithBehavior(DomElement element,
        double? left = null, double? top = null,
        bool relative = false, bool clamp = true, string? behavior = null);

    DomElement? GetOffsetParentForDomElement(DomElement element);
    DomElement? GetScrollParentForDomElement(DomElement element);

    (double Left, double Top, double Width, double Height) GetBoundingClientRectForDomElement(DomElement element, bool isRoot);

    (string Block, string Inline, string? Behavior) GetScrollIntoViewOptions(in JsCall call);
    void ScrollElementIntoView(DomElement element, string? block = null, string? inline = null, string? behavior = null);
    (double? Left, double? Top, string? Behavior) GetScrollOptions(in JsCall call);

    /// <summary>The single JS wrapper identity for <paramref name="node"/>.</summary>
    JsValue WrapNode(DomNode node);
}
