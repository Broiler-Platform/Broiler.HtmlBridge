using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

// Explicit IDocumentQueryHost implementation for the DocumentQueryBinding feature module (Phase 3):
// the bridge exposes the document root, the document-order element list and the JS-wrapper factory
// via explicit interface members, so the module never reaches an arbitrary bridge private field and
// the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IDocumentQueryHost
{
    Broiler.JavaScript.Engine.JSContext? Dom.Features.IDocumentQueryHost.JsContext => _jsContext;

    JSObject Dom.Features.IDocumentQueryHost.ToJSObject(DomNode node) => ToJSObject(node);

    DomElement Dom.Features.IDocumentQueryHost.DocumentElement => DocumentElement;

    IReadOnlyList<DomElement> Dom.Features.IDocumentQueryHost.Elements => Elements;

    bool Dom.Features.IDocumentQueryHost.MatchesSelector(DomElement element, string selector, DomElement? scope)
        => MatchesSelector(element, selector, scope);
}
