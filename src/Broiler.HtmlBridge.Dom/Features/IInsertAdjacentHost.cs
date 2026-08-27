using System.Collections.Generic;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The host surface <see cref="InsertAdjacentBinding"/> needs from the bridge for the DOM
/// <c>insertAdjacentElement</c> / <c>insertAdjacentText</c> / <c>insertAdjacentHTML</c> methods: the JS
/// context (so <c>DomBridge.ThrowDOMException</c> can raise the spec's <c>SyntaxError</c> /
/// <c>NoModificationAllowedError</c>), the JS-wrapper → element reverse lookup, the side-effecting insertion
/// primitive, the text-node factory, the fragment parser for the HTML variant, and the computed-style engine
/// reset the HTML variant performs after inserting parsed nodes. The neutral tree helpers the position
/// resolution uses (<c>ParentEl</c>, <c>ChildIndexOf</c>) stay the bridge's <c>internal static</c> helpers,
/// called directly.
/// </summary>
internal interface IInsertAdjacentHost
{
    JSContext? JsContext { get; }
    DomElement? FindDomElementByJSObject(JSObject jsObj);
    void InsertNodeAt(DomNode parent, DomNode node, int index);
    DomText CreateBridgeTextNode(string data);
    List<DomNode> BuildAdjacentHtmlNodes(DomElement contextElement, string html);
    void ResetComputedStyleEngines();
}
