using Broiler.HtmlBridge.Dom.Features;
using Broiler.HtmlBridge.Jseal;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="ICustomElementsHost"/> — the DOM services
/// the custom element registry needs, plus the realm it calls page code back through. Explicit
/// interface members, so calling into page code does not become part of the public <c>DomBridge</c>
/// surface.
/// </summary>
/// <remarks>
/// <para>
/// Six members are gone with the contract's migration: constructing a definition, calling a reaction
/// and the three promise factories were engine operations with no bridge state behind them, so the
/// registry asks the realm for them rather than asking the bridge to relay them. What is left is what
/// only the bridge can answer.
/// </para>
/// <para>
/// <see cref="Realm"/> is implemented explicitly because it has to be: <c>DomBridge.Realm</c> is
/// <see langword="internal"/>, and an implicit implementation of a public interface member typed
/// against it does not compile (CS0737).
/// </para>
/// </remarks>
public sealed partial class DomBridge : ICustomElementsHost
{
    IJsRealm ICustomElementsHost.Realm => Realm;

    IReadOnlyList<DomElement> ICustomElementsHost.Elements => Elements;

    // The bridge's own JSEAL-vocabulary wrapper factory: a handle over the object the engine-typed
    // wrapper cache already holds. It is a cast, not a conversion, so wrapper identity is unchanged
    // and the weak tables keyed on it keep keying on the same instances.
    JsValue ICustomElementsHost.WrapNode(DomNode node) => WrapNode(node);

    bool ICustomElementsHost.TryGetWrapper(DomElement element, out JsValue wrapper)
    {
        if (_jsObjects.TryGet(element, out var cached))
        {
            wrapper = cached;
            return true;
        }

        wrapper = JsValue.Missing;
        return false;
    }

    // A plain forward: the reverse lookup takes the same handle. The registry guards with IsObject
    // before it calls, and a handle that is not an object would answer null rather than throw.
    DomNode? ICustomElementsHost.FindNode(JsValue wrapper) =>
        FindDomNodeByJSObject(wrapper);

    DomElement ICustomElementsHost.CreateBridgeElement(string tagName) => CreateBridgeElement(tagName);

    /// <summary>
    /// Whether the element is in a document tree. The connected test walks to the root and asks
    /// whether it is a document, rather than testing for a parent: a subtree assembled off-tree
    /// has parents all the way up and is still not connected, and <c>connectedCallback</c> must not
    /// run for it.
    /// </summary>
    /// <remarks>
    /// Any document, not only the page's. A node adopted into a frame's or a
    /// <c>createHTMLDocument</c>'s tree is connected there, and a browser runs its
    /// <c>connectedCallback</c> — measured, the cross-document <c>appendChild</c> shape reports
    /// connected, disconnected, adopted, connected.
    /// </remarks>
    bool ICustomElementsHost.IsConnected(DomElement element)
    {
        for (DomNode? node = element; node is not null; node = node.ParentNode)
        {
            if (node is DomDocument)
                return true;
        }

        return false;
    }

    DomElement? ICustomElementsHost.FormOwnerOf(DomElement element) =>
        Dom.Features.FormAssociationBinding.FormOwnerOf(this, element);

    bool ICustomElementsHost.IsFormControlDisabled(DomElement element) => IsFormControlDisabled(element);
}
