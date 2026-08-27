using System.Collections.Generic;
using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

// Explicit IFormAssociationHost implementation for the FormAssociationBinding feature module: the
// wrapper factory, the document-order element list, the by-id lookup and the realm. Explicit
// interface members, so these seams do not widen the public DomBridge surface.
public sealed partial class DomBridge : Dom.Features.IFormAssociationHost
{
    JSObject Dom.Features.IFormAssociationHost.ToJSObject(DomNode node) => ToJSObject(node);

    IReadOnlyList<DomElement> Dom.Features.IFormAssociationHost.Elements => Elements;

    DomElement? Dom.Features.IFormAssociationHost.GetElementById(string id) =>
        FindInSubTree(DocumentElement, element => element.Id == id);

    Broiler.JavaScript.Engine.JSContext? Dom.Features.IFormAssociationHost.JsContext => _jsContext;

    bool Dom.Features.IFormAssociationHost.IsFormAssociatedCustomElement(DomElement element) =>
        CustomElements.IsFormAssociated(element);
}
