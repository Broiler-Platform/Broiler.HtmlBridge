using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

// Explicit INodeAccessorsHost implementation for the NodeAccessorsBinding feature module (Phase 3):
// the bridge exposes the JS-wrapper factory, the live childNodes collection, the document node, the
// tree-root walk, the notifying character-data setter and the two document-wrapper lookups via
// explicit interface members, so the module reaches no arbitrary bridge private field and the public
// surface is unchanged.
//
// This file is the engine-typed half of the seam. The module is written against JSEAL and speaks only
// JsValue; the wrapper cache, the NodeList factory and the document wrapper below still speak
// Broiler.JS, and they stop doing so when Runtime/JsObjectRegistry.cs and DomCollectionBinding
// migrate. The childNodes collection moved here from the module for the reason ISelectorsHost's two
// HTMLCollection builders did: assembling one is entirely engine-typed work today, and reassembling
// it from a list handed across the seam would convert every element of a live collection twice on
// every property read.
public sealed partial class DomBridge : Dom.Features.INodeAccessorsHost
{
    JsValue Dom.Features.INodeAccessorsHost.WrapNode(DomNode node) => WrapNode(node);

    JsValue Dom.Features.INodeAccessorsHost.ChildNodeList(DomNode node) =>
        Dom.Runtime.JsInterop.FromEngineObject((JSObject)Dom.Features.DomCollectionBinding.NodeList(_jsContext, () =>
        {
            var children = new List<JSValue>();
            foreach (var child in node.ChildNodes)
                children.Add(ToJSObject(child));

            return children;
        }));

    DomNode Dom.Features.INodeAccessorsHost.DocumentNode => _document;

    DomNode Dom.Features.INodeAccessorsHost.GetTreeRoot(DomNode node) => GetTreeRoot(node);

    void Dom.Features.INodeAccessorsHost.SetCharacterData(DomNode node, string? value)
        => SetCharacterData(node, value);

    bool Dom.Features.INodeAccessorsHost.TryGetDocumentWrapper(DomNode documentRoot, out JsValue wrapper)
    {
        if (_jsObjects.TryGetDocument(documentRoot, out var document))
        {
            wrapper = Dom.Runtime.JsInterop.FromEngineObject(document);
            return true;
        }

        wrapper = JsValue.Missing;
        return false;
    }

    // JsValue.Null rather than a nullable handle: `ownerDocument` coalesced the absent wrapper to
    // JavaScript null at its one call site, so answering that here is the same value by a shorter
    // route rather than a new decision. The wrapper itself comes from the bridge's sibling handle
    // (DomBridge.cs), which answers Missing when there is no document yet — the coalesce is this
    // contract's, not the field's.
    JsValue Dom.Features.INodeAccessorsHost.DocumentWrapper =>
        DocumentHandle is { IsMissing: false } document ? document : JsValue.Null;
}
