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
    // A plain forward: FindDomElementByJSObject takes the same handle, and the wrapper registry behind
    // it is keyed on JsValue.ObjectIdentity, not on the engine's own objects as this used to say.
    // There is no unwrap left to fail; a handle that is not an object answers null.
    DomElement? Dom.Features.IInsertAdjacentHost.FindElement(JsValue wrapper)
        => FindDomElementByJSObject(wrapper);

    void Dom.Features.IInsertAdjacentHost.InsertNodeAt(DomNode parent, DomNode node, int index) => InsertNodeAt(parent, node, index);
    DomText Dom.Features.IInsertAdjacentHost.CreateBridgeTextNode(string data) => CreateBridgeTextNode(data);
    List<DomNode> Dom.Features.IInsertAdjacentHost.BuildAdjacentHtmlNodes(DomElement contextElement, string html) => BuildAdjacentHtmlNodes(contextElement, html);
    void Dom.Features.IInsertAdjacentHost.ResetComputedStyleEngines() => ResetComputedStyleEngines();
}
