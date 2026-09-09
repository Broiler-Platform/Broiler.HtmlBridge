using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The bridge services <see cref="ElementInternalsBinding"/> consumes: the custom-element registry's
/// two questions (is this element custom, and did its definition declare <c>formAssociated</c>), the
/// form-association reads an <c>ElementInternals</c> answers with, and the one event it fires.
/// </summary>
/// <remarks>
/// The whole contract is spelled in JSEAL (<see cref="IJsRealm"/>), so nothing here names an engine
/// type. The script context it used to carry beside the realm was there for one thing — raising the
/// two <c>NotSupportedError</c>s <c>attachInternals</c> and the form-only members produce — and
/// <see cref="IJsCalls.DomError"/> owns that now. The realm stays because the module mints its two
/// interface prototypes, its per-instance objects and its coercions in it, and because
/// <c>RegisterInterfaces</c> runs with no call frame to carry one.
/// </remarks>
internal interface IElementInternalsHost
{
    /// <summary>The realm the three interfaces and every object they hand back are minted in.</summary>
    IJsRealm Realm { get; }

    /// <summary>The single JS wrapper identity for <paramref name="node"/>.</summary>
    JsValue WrapNode(DomNode node);

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
    JsValue LabelsFor(DomElement element);

    /// <summary>The element's shadow root, or <see cref="JsValue.Null"/>.</summary>
    JsValue ShadowRootOf(DomElement element);

    /// <summary>Fires a non-bubbling cancelable <c>invalid</c> event at the element, which is what
    /// <c>checkValidity</c> does when the element is invalid.</summary>
    void DispatchInvalidEvent(DomElement element);
}
