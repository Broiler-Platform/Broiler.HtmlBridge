using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit IDocumentCollectionHost implementation for the DocumentCollectionBinding feature module
// (Phase 3): the bridge exposes its realm, the element list, the JS-wrapper factory and the
// stylesheet-object builder via explicit interface members, so the module never reaches an arbitrary
// bridge private field and the public surface is unchanged.
//
// Explicit, including Realm: DomBridge.Realm is internal, so an implicit implementation of a member
// on an internal interface would be CS0737.
//
// The wrapper factory answers a handle now, so the member below forwards to it. The unqualified name
// resolves to the bridge's own internal WrapNode (DomBridge/JsObjects.cs) and not to this explicit
// implementation, which is reachable only through the interface -- so this is a forward, not a
// recursion. The stylesheet builder answers a handle too, so it is a second such forward: the
// collection is built over the cached sheet objects themselves and sheet identity is untouched.
public sealed partial class DomBridge : Dom.Features.IDocumentCollectionHost
{
    IJsRealm Dom.Features.IDocumentCollectionHost.Realm => Realm;

    JsValue Dom.Features.IDocumentCollectionHost.WrapNode(DomNode node) => WrapNode(node);

    IReadOnlyList<DomElement> Dom.Features.IDocumentCollectionHost.Elements => Elements;

    int Dom.Features.IDocumentCollectionHost.CurrentScriptIndex => CurrentScriptIndex;

    JsValue Dom.Features.IDocumentCollectionHost.BuildStyleSheetObject(DomElement styleElement)
        => BuildStyleSheet(styleElement);

    bool Dom.Features.IDocumentCollectionHost.HasAssociatedStyleSheet(DomElement element)
        => HasAssociatedStyleSheet(element);
}
