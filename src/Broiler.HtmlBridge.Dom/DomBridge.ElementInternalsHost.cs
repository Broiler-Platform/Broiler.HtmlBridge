using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IElementInternalsHost"/> — the
/// custom-element and form-association reads a form-associated custom element's
/// <c>ElementInternals</c> answers with. Explicit interface members, so the seam does not widen the
/// public <c>DomBridge</c> surface.
/// </summary>
public sealed partial class DomBridge : IElementInternalsHost
{
    private ElementInternalsBinding? _elementInternals;

    internal ElementInternalsBinding ElementInternals =>
        _elementInternals ??= new ElementInternalsBinding(this);

    JSContext IElementInternalsHost.JsContext => _jsContext!;

    JSObject IElementInternalsHost.ToJSObject(DomNode node) => ToJSObject(node);

    bool IElementInternalsHost.IsCustomElement(DomElement element) => CustomElements.IsCustom(element);

    bool IElementInternalsHost.IsFormAssociatedCustomElement(DomElement element) =>
        CustomElements.IsFormAssociated(element);

    DomElement? IElementInternalsHost.FormOwnerOf(DomElement element) =>
        FormAssociationBinding.FormOwnerOf(this, element);

    bool IElementInternalsHost.IsDisabled(DomElement element) => IsFormControlDisabled(element);

    JSValue IElementInternalsHost.LabelsFor(DomElement element) =>
        FormAssociationBinding.LabelsNodeList(this, element);

    /// <summary>
    /// The element's shadow root. An internals reports the same root <c>element.shadowRoot</c> does —
    /// it is the element's own — so this goes through the one implementation rather than a second.
    /// </summary>
    JSValue IElementInternalsHost.ShadowRootOf(DomElement element)
    {
        var noArguments = new Arguments(JSUndefined.Value);
        return ShadowDomBinding.GetShadowRoot(this, element, in noArguments);
    }

    /// <summary>
    /// Fires a non-bubbling cancelable <c>invalid</c> event at the element — what
    /// <c>checkValidity</c> does when it is about to answer <see langword="false"/>, and how a page
    /// hears about a failed control without polling every one of them.
    /// </summary>
    void IElementInternalsHost.DispatchInvalidEvent(DomElement element)
    {
        var evt = new JSObject();
        evt.FastAddValue("type", new JSString("invalid"), JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("bubbles", JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
        evt.FastAddValue("cancelable", JSBoolean.True, JSPropertyAttributes.EnumerableConfigurableValue);
        DispatchEventOnElement(element, evt);
    }
}
