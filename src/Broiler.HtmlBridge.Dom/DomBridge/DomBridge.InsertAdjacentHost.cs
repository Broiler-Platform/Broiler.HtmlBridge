using System.Collections.Generic;
using Broiler.HtmlBridge.Jseal;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

// Explicit IInsertAdjacentHost implementation for the InsertAdjacentBinding feature module (Phase 3): the
// insertAdjacent* methods reach the bridge through this seam — the wrapper reverse lookup, the insertion
// primitive, the text-node factory, the fragment parser, and the computed-style reset — while the neutral
// tree helpers (ParentEl/ChildIndexOf) are internal statics.
//
// The JS context the contract used to carry is gone with the module's migration: it was there only so
// DomBridge.ThrowDOMException could raise the SyntaxError / NoModificationAllowedError, and the module
// now raises those through its own call frame's realm.
public sealed partial class DomBridge : Dom.Features.IInsertAdjacentHost
{
    // The module only asks this of a handle it has already established is an object, so unwrapping
    // cannot fail here. FindDomElementByJSObject is the unmigrated half — the wrapper registry is
    // keyed on the engine's own objects — and the seam is a cast, not a conversion.
    DomElement? Dom.Features.IInsertAdjacentHost.FindElement(JsValue wrapper)
        => FindDomElementByJSObject(Dom.Runtime.JsInterop.ToEngineObject(wrapper));

    void Dom.Features.IInsertAdjacentHost.InsertNodeAt(DomNode parent, DomNode node, int index) => InsertNodeAt(parent, node, index);
    DomText Dom.Features.IInsertAdjacentHost.CreateBridgeTextNode(string data) => CreateBridgeTextNode(data);
    List<DomNode> Dom.Features.IInsertAdjacentHost.BuildAdjacentHtmlNodes(DomElement contextElement, string html) => BuildAdjacentHtmlNodes(contextElement, html);
    void Dom.Features.IInsertAdjacentHost.ResetComputedStyleEngines() => ResetComputedStyleEngines();
}
