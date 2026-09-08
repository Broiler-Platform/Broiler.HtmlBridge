using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

// Explicit INodeRelationshipsHost implementation for the NodeRelationshipsBinding feature module
// (Phase 3): the bridge exposes the wrapper→node resolver, the tree-root walk, the character-data-aware
// normalize(), the root-node wrapper factory, the clone and the plain JS-wrapper factory via explicit
// interface members, so the module reaches no arbitrary bridge private field and the public surface is
// unchanged.
//
// This file is the engine-typed half of the seam. The module speaks JSEAL and hands back JsValue
// handles; the reverse wrapper lookup and the document wrapper below still speak Broiler.JS, and they
// stop doing so when DomBridge/Utilities.cs and DomBridge/ShadowDom.cs migrate. The cast is a cast and
// not a conversion — a JSEAL object handle carries the engine's own object — so wrapper identity is
// the same question it was before.
//
// The JsContext member is gone: it was here only so the module could raise the DOMException a document
// clone must throw, and IJsCalls.DomError owns that now.
public sealed partial class DomBridge : Dom.Features.INodeRelationshipsHost
{
    DomNode? Dom.Features.INodeRelationshipsHost.FindNode(JsValue wrapper)
        => wrapper.IsObject ? FindDomNodeByJSObject(Dom.Runtime.JsInterop.ToEngineObject(wrapper)) : null;

    DomNode Dom.Features.INodeRelationshipsHost.GetTreeRoot(DomNode node) => GetTreeRoot(node);

    void Dom.Features.INodeRelationshipsHost.NormalizeNode(DomElement element) => NormalizeNode(element);

    // ToJSRootNode answers a wrapper or the engine's null (a document with no wrapper yet), which is
    // the same pair of arms FromEngineResult exists for in the selectors seam.
    JsValue Dom.Features.INodeRelationshipsHost.WrapRootNode(DomNode root)
        => ToJSRootNode(root) is JSObject wrapper
            ? Dom.Runtime.JsInterop.FromEngineObject(wrapper)
            : JsValue.Null;

    DomNode Dom.Features.INodeRelationshipsHost.CloneDomElement(DomNode source, bool deep)
        => CloneDomElement(source, deep);

    JsValue Dom.Features.INodeRelationshipsHost.WrapNode(DomNode node) => WrapNode(node);
}
