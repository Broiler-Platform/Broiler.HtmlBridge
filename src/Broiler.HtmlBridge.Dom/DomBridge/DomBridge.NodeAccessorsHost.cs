using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit INodeAccessorsHost implementation for the NodeAccessorsBinding feature module (Phase 3):
// the bridge exposes the JS-wrapper factory, the live childNodes collection, the document node, the
// tree-root walk, the notifying character-data setter and the two document-wrapper lookups via
// explicit interface members, so the module reaches no arbitrary bridge private field and the public
// surface is unchanged.
//
// Nothing here is engine-typed any more: the module speaks JsValue, the NodeList factory takes the
// realm and the same handles, and the wrapper cache is reached through WrapNode. The childNodes
// collection stays in this file rather than going back to the module for the reason ISelectorsHost's
// two HTMLCollection builders stay: a live collection recomputes its contents on every property read,
// and the bridge is where the child list lives.
public sealed partial class DomBridge : Dom.Features.INodeAccessorsHost
{
    JsValue Dom.Features.INodeAccessorsHost.WrapNode(DomNode node) => WrapNode(node);

    JsValue Dom.Features.INodeAccessorsHost.ChildNodeList(DomNode node) =>
        Dom.Features.DomCollectionBinding.NodeList(Realm, () =>
        {
            var children = new List<JsValue>();
            foreach (var child in node.ChildNodes)
                children.Add(WrapNode(child));

            return children;
        });

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
