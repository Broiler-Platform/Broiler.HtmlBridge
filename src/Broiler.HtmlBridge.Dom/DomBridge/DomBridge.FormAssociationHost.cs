using System;
using System.Collections.Generic;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit IFormAssociationHost implementation for the FormAssociationBinding feature module: the
// realm, the wrapper factory, the document-order element list, the by-id lookup and the live NodeList
// a control's `labels` is. Explicit interface members, so these seams do not widen the public
// DomBridge surface.
//
// The whole seam is spelled in JSEAL now. The collection was minted through DomCollectionBinding's
// engine-typed entry point and unwrapped back across JsInterop while that module was unmigrated; it
// has a realm-shaped NodeList of its own, so the list is built and handed on without either
// conversion. Realm must be an explicit implementation — DomBridge.Realm is internal, so an implicit
// one does not compile (CS0737).
public sealed partial class DomBridge : Dom.Features.IFormAssociationHost
{
    IJsRealm Dom.Features.IFormAssociationHost.Realm => Realm;

    JsValue Dom.Features.IFormAssociationHost.WrapNode(DomNode node) => WrapNode(node);

    IReadOnlyList<DomElement> Dom.Features.IFormAssociationHost.Elements => Elements;

    DomElement? Dom.Features.IFormAssociationHost.GetElementById(string id) =>
        FindInSubTree(DocumentElement, element => element.Id == id);

    JsValue Dom.Features.IFormAssociationHost.LiveNodeList(Func<List<DomElement>> contents) =>
        Dom.Features.DomCollectionBinding.NodeList(
            Realm,
            // Re-run on every read, and the wrapping with it: a label created since the list was
            // handed out has no wrapper yet, so wrapping here rather than at build time is what
            // keeps the list live rather than merely re-counted.
            () => [.. contents().Select(WrapNode)]);

    bool Dom.Features.IFormAssociationHost.IsFormAssociatedCustomElement(DomElement element) =>
        CustomElements.IsFormAssociated(element);
}
