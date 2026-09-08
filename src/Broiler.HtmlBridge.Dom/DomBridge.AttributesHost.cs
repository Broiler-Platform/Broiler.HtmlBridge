using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IAttributesHost"/>, the narrow contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.AttributesBinding"/> feature module consumes
/// (HtmlBridge complexity-reduction roadmap Phase 3, P3.12). Each member is an explicit interface
/// implementation, so these cross-cutting seams (CSSOM inline style, the Events inline-handler
/// compiler, the CSS invalidation route and the MutationObserver notification) do not widen the public
/// <c>DomBridge</c> surface.
/// </summary>
public sealed partial class DomBridge : IAttributesHost
{
    IJsRealm IAttributesHost.Realm => Realm;

    // The seam's last engine reference, and it is the name validators' rather than the module's: they
    // mint the InvalidCharacterError DOMException from a script context. See IAttributesHost.JsContext.
    Broiler.JavaScript.Engine.JSContext? IAttributesHost.JsContext => _jsContext;

    void IAttributesHost.ApplyStyleAttribute(DomElement element, string value)
    {
        InlineStyle(element).Clear();
        foreach (var kv in ParseStyle(value, reportDrops: true))
            InlineStyle(element)[kv.Key] = kv.Value;
        InvalidateStyleScope(element);
    }

    void IAttributesHost.CompileInlineEventAttribute(DomElement element, string attributeName, string code) =>
        CompileInlineEventAttribute(element, attributeName, code);

    void IAttributesHost.InvalidateStyleScope(DomElement element) => InvalidateStyleScope(element);

    void IAttributesHost.NotifyAttributeMutationObservers(DomElement element, string attributeName, string? oldValue) =>
        NotifyAttributeMutationObservers(element, attributeName, oldValue);

    /// <remarks>
    /// The wrapper crosses as a JSEAL handle and is unwrapped to the engine object the bridge's
    /// prototype table is keyed on — a cast, not a conversion, because the handle carries that very
    /// object.
    /// </remarks>
    void IAttributesHost.LinkToInterface(JsValue wrapper, string interfaceName) =>
        LinkToInterface(Dom.Runtime.JsInterop.ToEngineObject(wrapper), interfaceName);
}
