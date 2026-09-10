using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IElementInternalsHost"/> — the
/// custom-element and form-association reads a form-associated custom element's
/// <c>ElementInternals</c> answers with. Explicit interface members, so the seam does not widen the
/// public <c>DomBridge</c> surface.
/// </summary>
/// <remarks>
/// <para>
/// The contract is spelled in JSEAL; this file is where that meets the half of the bridge that is
/// still engine-typed. <see cref="Dom.Runtime.JsInterop"/> is the cast between them and not a
/// conversion — a JSEAL object handle carries the engine's own object — so wrapper identity is the
/// same question it was before.
/// </para>
/// <para>
/// <see cref="Realm"/> is implemented explicitly because it has to be: <c>DomBridge.Realm</c> is
/// <see langword="internal"/>, and an implicit implementation of an interface member typed against it
/// does not compile (CS0737).
/// </para>
/// </remarks>
public sealed partial class DomBridge : IElementInternalsHost
{
    private ElementInternalsBinding? _elementInternals;

    internal ElementInternalsBinding ElementInternals =>
        _elementInternals ??= new ElementInternalsBinding(this);

    IJsRealm IElementInternalsHost.Realm => Realm;

    JsValue IElementInternalsHost.WrapNode(DomNode node) => WrapNode(node);

    bool IElementInternalsHost.IsCustomElement(DomElement element) => CustomElements.IsCustom(element);

    bool IElementInternalsHost.IsFormAssociatedCustomElement(DomElement element) =>
        CustomElements.IsFormAssociated(element);

    DomElement? IElementInternalsHost.FormOwnerOf(DomElement element) =>
        FormAssociationBinding.FormOwnerOf(this, element);

    bool IElementInternalsHost.IsDisabled(DomElement element) => IsFormControlDisabled(element);

    JsValue IElementInternalsHost.LabelsFor(DomElement element) =>
        FormAssociationBinding.LabelsList(this, element);

    /// <summary>
    /// The element's shadow root. An internals reports the same root <c>element.shadowRoot</c> does —
    /// it is the element's own — so this goes through the one implementation rather than a second.
    /// </summary>
    /// <remarks>
    /// The empty argument frame this used to build is gone with the shadow module's migration: the
    /// getter never read one, so the JSEAL entry point does not take one.
    /// </remarks>
    JsValue IElementInternalsHost.ShadowRootOf(DomElement element) =>
        ShadowDomBinding.GetShadowRoot(this, element);

    /// <summary>
    /// Fires a non-bubbling cancelable <c>invalid</c> event at the element — what
    /// <c>checkValidity</c> does when it is about to answer <see langword="false"/>, and how a page
    /// hears about a failed control without polling every one of them.
    /// </summary>
    /// <remarks>
    /// The event object is built through the realm; its three members keep the
    /// enumerable/configurable data-property attributes they had, which is what
    /// <see cref="JsPropertyFlags.Default"/> spells. Dispatch is still engine-typed, so the handle is
    /// unwrapped at that one call through the <see cref="Dom.Runtime.JsInterop"/> seam.
    /// </remarks>
    void IElementInternalsHost.DispatchInvalidEvent(DomElement element)
    {
        var evt = Realm.NewObject();
        Realm.DefineValue(evt, "type", JsValue.String("invalid"));
        Realm.DefineValue(evt, "bubbles", JsValue.False);
        Realm.DefineValue(evt, "cancelable", JsValue.True);
        _eventDispatch.DispatchEventOnElement(element, evt);
    }
}
