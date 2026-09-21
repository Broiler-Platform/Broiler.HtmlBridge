using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.JSeal;
using static Broiler.HtmlBridge.DomBridgeHostUtils;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of the shared host primitives declared in
/// <c>Features/IBridgeHostPrimitives.cs</c> — the handful of services most of the narrow
/// <c>I*Host</c> contracts need, which each contract used to re-declare and each of which
/// DomBridge used to implement once per contract.
/// <para>
/// Every member here is an explicit interface implementation, which is not a style choice: the
/// instance members they forward to are <c>internal</c>, and an implicit implementation of an
/// interface member has to be public (CS0737). Explicit implementations also keep these off
/// <c>DomBridge</c>'s own surface, which is what the per-contract forwarders did before.
/// </para>
/// <para>
/// <see cref="IElementsHost.Elements"/> and <see cref="IDocumentElementHost.DocumentElement"/> are
/// absent deliberately: <c>DomBridge</c> already exposes public members of those names and shapes,
/// so they are satisfied implicitly, exactly as the per-contract forwarders satisfied them.
/// </para>
/// </summary>
public sealed partial class DomBridge
{
    IJsRealm IRealmHost.Realm => Realm;

    JsValue INodeWrapperHost.WrapNode(DomNode node) => WrapNode(node);

    /// <remarks>The older name for the same wrapper; the contracts that ask for it want one object per node.</remarks>
    JsValue IJsObjectHost.ToJsObject(DomNode node) => WrapNode(node);

    void IStyleInvalidationHost.InvalidateStyleScope(DomElement anchor) => InvalidateStyleScope(anchor);

    DomNode IDocumentNodeHost.DocumentNode => _document;

    string IPageUrlHost.PageUrl => _pageUrl;

    /// <remarks>
    /// The realm is threaded in because a name check raises a <c>DOMException</c> through it, which is
    /// why these two do not simply forward their arguments.
    /// </remarks>
    void INameValidationHost.ValidateElementName(string name) => ValidateElementName(name, Realm);

    /// <inheritdoc cref="INameValidationHost.ValidateElementName"/>
    void INameValidationHost.ValidateQualifiedName(string qualifiedName, string? ns) =>
        ValidateQualifiedName(qualifiedName, ns, Realm);

    // `scope` carries no default here: the interface declares it optional, and repeating the default
    // on the implementation is CS1066.
    bool ISelectorMatchHost.MatchesSelector(DomElement element, string selector, DomElement? scope) =>
        MatchesSelector(element, selector, scope);

    void ISelectorMatchHost.ValidateSelector(string selector) => ValidateSelector(selector);

    DomText ITextNodeFactoryHost.CreateBridgeTextNode(string data) => CreateBridgeTextNode(data);

    void INodeInsertionHost.InsertNodeAt(DomNode parent, DomNode node, int index) =>
        InsertNodeAt(parent, node, index);

    JsValue ISubDocumentFactoryHost.GetOrCreateSubDocument(DomElement container) =>
        GetOrCreateSubDocument(container);
}
