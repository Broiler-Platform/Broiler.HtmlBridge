using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit IDocumentQueryHost implementation for the DocumentQueryBinding feature module (Phase 3):
// the bridge exposes the document root, the document-order element list, the JS-wrapper factory,
// selector validation and the two collection factories via explicit interface members, so the module
// never reaches an arbitrary bridge private field and the public surface is unchanged.
//
// The contract above is spelled in JSEAL and so is everything this file forwards to: the wrapper
// factory answers a handle, the collection builder takes the realm and the module's own list, and the
// selector validation raises its DOMException through the realm. Nothing is unwrapped here any more,
// which is why the two adapters that used to convert a collection's contents in both directions on
// every property read are gone rather than moved.
public sealed partial class DomBridge : Dom.Features.IDocumentQueryHost
{
    JsValue Dom.Features.IDocumentQueryHost.ToJsObject(DomNode node) => WrapNode(node);

    DomElement Dom.Features.IDocumentQueryHost.DocumentElement => DocumentElement;

    IReadOnlyList<DomElement> Dom.Features.IDocumentQueryHost.Elements => Elements;

    bool Dom.Features.IDocumentQueryHost.MatchesSelector(DomElement element, string selector, DomElement? scope)
        => MatchesSelector(element, selector, scope);

    // The realm is what the DOMException is constructed against, and having none (no bridge attached
    // yet) means the validation is skipped — exactly as when the module passed host.JsContext straight
    // back to this helper and a null context meant the same thing.
    void Dom.Features.IDocumentQueryHost.ValidateSelector(string selector) =>
        ValidateSelector(selector);

    JsValue Dom.Features.IDocumentQueryHost.NodeList(Func<List<JsValue>> contents) =>
        Dom.Features.DomCollectionBinding.NodeList(Realm, contents);

    JsValue Dom.Features.IDocumentQueryHost.HtmlCollection(
        Func<List<JsValue>> contents, Func<string, JsValue?>? namedLookup) =>
        Dom.Features.DomCollectionBinding.HtmlCollection(Realm, contents, namedLookup);
}
