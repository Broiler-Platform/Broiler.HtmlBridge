using System;
using System.Collections.Generic;
using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit IFormAssociationHost implementation for the FormAssociationBinding feature module: the
// realm, the wrapper factory, the document-order element list, the by-id lookup and the live NodeList
// a control's `labels` is. Explicit interface members, so these seams do not widen the public
// DomBridge surface.
//
// This is the half-migrated seam for the form-association slice. The module speaks JSEAL; the
// NodeList is still built by DomCollectionBinding, which is not migrated, so the collection is minted
// on this side — from the wrappers the bridge already caches — and crosses through JsInterop, which
// carries the object without converting it.
public sealed partial class DomBridge : Dom.Features.IFormAssociationHost
{
    IJsRealm Dom.Features.IFormAssociationHost.Realm => Realm;

    JsValue Dom.Features.IFormAssociationHost.WrapNode(DomNode node) =>
        JsInterop.FromEngineObject(ToJSObject(node));

    IReadOnlyList<DomElement> Dom.Features.IFormAssociationHost.Elements => Elements;

    DomElement? Dom.Features.IFormAssociationHost.GetElementById(string id) =>
        FindInSubTree(DocumentElement, element => element.Id == id);

    JsValue Dom.Features.IFormAssociationHost.LiveNodeList(Func<List<DomElement>> contents) =>
        JsInterop.FromEngineObject(
            (Broiler.JavaScript.Runtime.JSObject)Dom.Features.DomCollectionBinding.NodeList(
                _jsContext,
                // Re-run on every read, and the wrapping with it: a label created since the list was
                // handed out has no wrapper yet, so wrapping here rather than at build time is what
                // keeps the list live rather than merely re-counted.
                () => [.. contents().Select(element => (Broiler.JavaScript.Runtime.JSValue)ToJSObject(element))]));

    bool Dom.Features.IFormAssociationHost.IsFormAssociatedCustomElement(DomElement element) =>
        CustomElements.IsFormAssociated(element);
}
