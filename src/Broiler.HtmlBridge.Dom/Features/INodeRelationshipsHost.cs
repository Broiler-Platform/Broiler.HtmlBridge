using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="NodeRelationshipsBinding"/> needs from the bridge: the
/// JS-object→node resolver (<c>contains</c>/<c>compareDocumentPosition</c>/<c>isSameNode</c>/
/// <c>isEqualNode</c> all take another wrapper), the tree-root walk (document-position + <c>getRootNode</c>),
/// the character-data-aware <c>normalize()</c>, the root-node wrapper factory, the deep/shallow clone and
/// the plain JS-wrapper factory. Pure tree predicates (<c>IsDescendantOf</c>, <c>IsEqualNode</c>) live on
/// <see cref="DomNode"/>; document-order comparison (<c>CompareTreeOrder</c>) and the shadow-root walk
/// (<c>FindContainingShadowRoot</c>) are the bridge's <c>internal static</c> helpers, called directly.
/// </summary>
internal interface INodeRelationshipsHost
{
    DomNode? FindDomNodeByJSObject(JSObject jsObj);
    DomNode GetTreeRoot(DomNode node);
    void NormalizeNode(DomElement element);
    JSValue ToJSRootNode(DomNode root);
    DomNode CloneDomElement(DomNode source, bool deep);
    JSObject ToJSObject(DomNode node);

    /// <summary>The realm, for raising a <c>DOMException</c> that page script can branch on.</summary>
    Broiler.JavaScript.Engine.JSContext? JsContext { get; }
}
