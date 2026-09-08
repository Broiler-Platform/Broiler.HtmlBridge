using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Runtime;
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
// The two wrapper members hand back JSEAL handles over the bridge's own engine objects. JsInterop is
// a cast and not a conversion, so the collection is built over the same wrapper instances it always
// was and wrapper identity is untouched; ToJSObject and BuildStyleSheetObject keep their engine-typed
// names because they are the bridge's own members, called from the two hundred sites this group does
// not own.
public sealed partial class DomBridge : Dom.Features.IDocumentCollectionHost
{
    IJsRealm Dom.Features.IDocumentCollectionHost.Realm => Realm;

    JsValue Dom.Features.IDocumentCollectionHost.WrapNode(DomNode node) =>
        JsInterop.FromEngineObject(ToJSObject(node));

    IReadOnlyList<DomElement> Dom.Features.IDocumentCollectionHost.Elements => Elements;

    int Dom.Features.IDocumentCollectionHost.CurrentScriptIndex => CurrentScriptIndex;

    JsValue Dom.Features.IDocumentCollectionHost.BuildStyleSheetObject(DomElement styleElement)
        => JsInterop.FromEngineObject(BuildStyleSheetObject(styleElement));

    bool Dom.Features.IDocumentCollectionHost.HasAssociatedStyleSheet(DomElement element)
        => HasAssociatedStyleSheet(element);
}
