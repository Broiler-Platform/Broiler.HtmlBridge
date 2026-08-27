using Broiler.Dom;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The bridge services <see cref="ElementInternalsBinding"/> consumes: the custom-element registry's
/// two questions (is this element custom, and did its definition declare <c>formAssociated</c>), the
/// form-association reads an <c>ElementInternals</c> answers with, and the one event it fires.
/// </summary>
internal interface IElementInternalsHost
{
    JSContext JsContext { get; }

    JSObject ToJSObject(DomNode node);

    /// <summary>Whether the element is a custom element — the gate on <c>attachInternals</c>, which
    /// a browser refuses for an ordinary one.</summary>
    bool IsCustomElement(DomElement element);

    /// <summary>Whether the element's definition declared <c>static formAssociated = true</c>. Every
    /// form-related member of <c>ElementInternals</c> is a <c>NotSupportedError</c> without it.</summary>
    bool IsFormAssociatedCustomElement(DomElement element);

    /// <summary>The element's form owner, or <see langword="null"/>.</summary>
    DomElement? FormOwnerOf(DomElement element);

    /// <summary>Whether the element is disabled — by its own <c>disabled</c> attribute or by an
    /// ancestor <c>&lt;fieldset disabled&gt;</c>. It is what decides <c>willValidate</c>.</summary>
    bool IsDisabled(DomElement element);

    /// <summary>The element's live <c>labels</c> list.</summary>
    JSValue LabelsFor(DomElement element);

    /// <summary>The element's shadow root, or <c>null</c>.</summary>
    JSValue ShadowRootOf(DomElement element);

    /// <summary>Fires a non-bubbling cancelable <c>invalid</c> event at the element, which is what
    /// <c>checkValidity</c> does when the element is invalid.</summary>
    void DispatchInvalidEvent(DomElement element);
}
