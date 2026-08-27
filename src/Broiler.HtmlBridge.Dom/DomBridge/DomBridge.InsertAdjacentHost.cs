using System.Collections.Generic;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

// Explicit IInsertAdjacentHost implementation for the InsertAdjacentBinding feature module (Phase 3): the
// insertAdjacent* methods reach the bridge through this seam — the JS context (for DOMException), the
// JS-wrapper reverse lookup, the insertion primitive, the text-node factory, the fragment parser, and the
// computed-style reset — while the neutral tree helpers (ParentEl/ChildIndexOf) are internal statics.
public sealed partial class DomBridge : Dom.Features.IInsertAdjacentHost
{
    JSContext? Dom.Features.IInsertAdjacentHost.JsContext => _jsContext;
    DomElement? Dom.Features.IInsertAdjacentHost.FindDomElementByJSObject(JSObject jsObj) => FindDomElementByJSObject(jsObj);
    void Dom.Features.IInsertAdjacentHost.InsertNodeAt(DomNode parent, DomNode node, int index) => InsertNodeAt(parent, node, index);
    DomText Dom.Features.IInsertAdjacentHost.CreateBridgeTextNode(string data) => CreateBridgeTextNode(data);
    List<DomNode> Dom.Features.IInsertAdjacentHost.BuildAdjacentHtmlNodes(DomElement contextElement, string html) => BuildAdjacentHtmlNodes(contextElement, html);
    void Dom.Features.IInsertAdjacentHost.ResetComputedStyleEngines() => ResetComputedStyleEngines();
}
