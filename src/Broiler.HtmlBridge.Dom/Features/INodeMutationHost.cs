using Broiler.Dom;
using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="NodeMutationBinding"/> needs from the bridge: the document
/// node (the mutation target), the JS-wrapper factory and reverse lookup, and the mutation-observer /
/// node-iterator notifications. The structural tree operations (index-of, insert, remove-nth,
/// set-parent, child enumeration) are the bridge's neutral <c>internal static</c> helpers, called
/// directly.
/// </summary>
internal interface INodeMutationHost
{
    JSObject ToJSObject(DomNode node);
    DomNode DocumentNode { get; }
    DomNode? FindDomNodeByJSObject(JSObject jsObj);

    /// <summary>
    /// Coerces a <c>(Node or DOMString)...</c> argument list to nodes, wrapping each string
    /// as a text node — the conversion the ParentNode mixin's <c>append</c>/<c>prepend</c>/
    /// <c>replaceChildren</c> share with their element-level counterparts.
    /// </summary>
    List<DomNode> BuildChildNodeArgumentNodes(in Arguments arguments);

    /// <summary>The realm the bindings run in, for throwing a <c>DOMException</c>.</summary>
    JSContext? JsContext { get; }

    void NotifyNodeIteratorPreRemoval(DomNode node);
    void NotifyChildRemoved(DomNode parent, DomNode child, int index);
    void NotifyChildAdded(DomNode parent, DomNode child, int index);
}
