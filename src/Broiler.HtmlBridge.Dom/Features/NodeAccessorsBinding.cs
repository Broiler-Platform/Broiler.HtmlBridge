using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Phase 3 feature module for the DOM <c>Node</c> read accessors shared by every node wrapper —
/// <c>isConnected</c>, <c>childNodes</c>, <c>firstChild</c>/<c>lastChild</c>,
/// <c>nextSibling</c>/<c>previousSibling</c>, <c>nodeType</c>/<c>nodeName</c>, <c>localName</c>/
/// <c>prefix</c>/<c>namespaceURI</c>, <c>nodeValue</c> (get/set), <c>publicId</c>/<c>systemId</c>
/// (DocumentType), <c>ownerDocument</c> and <c>parentElement</c>. These were the bridge's
/// <c>JsJsObjectsGet…032</c>..<c>058</c> callbacks; the JS-wrapper factory, the live <c>childNodes</c>
/// collection, the document node, the tree-root walk, the notifying character-data setter and the
/// document-wrapper lookups reach the bridge through <see cref="INodeAccessorsHost"/>, while node-type
/// tests, tree-order helpers, text reads and the owning-document derivation are the bridge's
/// <c>internal static</c> helpers.
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine
/// type. Only one member reads an argument at all — <see cref="SetNodeValue"/> — and it asks the realm
/// for the coercion rather than rendering the handle: an object assigned to <c>nodeValue</c> runs its
/// own <c>toString</c>, which is what a page observes and what <c>JsValue.ToString()</c> would not do.
/// </remarks>
internal static class NodeAccessorsBinding
{
    public static JsValue GetIsConnected(INodeAccessorsHost host, DomNode node, in JsCall call)
    {
        var root = host.GetTreeRoot(node);
        return JsValue.Boolean(ReferenceEquals(root, host.DocumentNode));
    }

    /// <summary>
    /// <c>node.childNodes</c> — a <b>live</b> <c>NodeList</c> (DOM §4.4). It used to be a plain
    /// array, which is a snapshot: <c>var kids = el.childNodes; el.appendChild(x); kids.length</c>
    /// grew in a browser and did not here, silently answering a stale number rather than failing.
    /// The list holds the walk rather than its result, so every read sees the tree as it is now.
    /// </summary>
    /// <remarks>
    /// Assembling that collection is <see cref="INodeAccessorsHost.ChildNodeList"/>'s: it is still
    /// engine-typed work in <c>DomCollectionBinding</c>, and handing this module a list to reassemble
    /// would convert every element of a live collection twice on every property read.
    /// </remarks>
    public static JsValue GetChildNodes(INodeAccessorsHost host, DomNode node, in JsCall call) =>
        host.ChildNodeList(node);

    public static JsValue GetFirstChild(INodeAccessorsHost host, DomNode node, in JsCall call)
    {
        var first = node.ChildNodes.FirstOrDefault();
        return first != null ? host.WrapNode(first) : JsValue.Null;
    }

    public static JsValue GetLastChild(INodeAccessorsHost host, DomNode node, in JsCall call)
    {
        var last = node.ChildNodes.LastOrDefault();
        return last != null ? host.WrapNode(last) : JsValue.Null;
    }

    public static JsValue GetNextSibling(INodeAccessorsHost host, DomNode node, in JsCall call)
    {
        var parent = node.ParentNode;
        if (parent == null)
            return JsValue.Null;
        var siblings = parent.ChildNodes;
        var idx = DomBridge.ChildIndexOf(parent, node);
        return idx >= 0 && idx + 1 < siblings.Count ? host.WrapNode(siblings[idx + 1]) : JsValue.Null;
    }

    public static JsValue GetPreviousSibling(INodeAccessorsHost host, DomNode node, in JsCall call)
    {
        var parent = node.ParentNode;
        if (parent == null)
            return JsValue.Null;
        var siblings = parent.ChildNodes;
        var idx = DomBridge.ChildIndexOf(parent, node);
        return idx - 1 >= 0 ? host.WrapNode(siblings[idx - 1]) : JsValue.Null;
    }

    public static JsValue GetNodeType(DomNode node, in JsCall call)
        // The canonical DomNodeType enum values ARE the DOM node-type constants
        // (Element=1, Text=3, Comment=8, Document=9, DocumentType=10, DocumentFragment=11).
        => JsValue.Number((int)node.NodeType);

    public static JsValue GetNodeName(DomNode node, in JsCall call)
    {
        if (DomBridge.IsText(node))
            return JsValue.String("#text");
        if (DomBridge.IsComment(node))
            return JsValue.String("#comment");
        if (node is DomDocumentType docType)
            return JsValue.String(docType.Name); // doctype nodeName is its (already lowercased) name
        if (node is DomDocumentFragment)
            return JsValue.String("#document-fragment");
        if (node is DomDocument)
            return JsValue.String("#document"); // canonical DomDocument — the document root
        if (node is not DomElement element)
            return JsValue.Null;

        // Non-HTML namespace elements preserve original case (per DOM spec)
        if (!string.IsNullOrEmpty(element.NamespaceUri) && !string.Equals(element.NamespaceUri, "http://www.w3.org/1999/xhtml", StringComparison.OrdinalIgnoreCase))
            return JsValue.String(element.TagName);
        return JsValue.String(element.TagName.ToUpperInvariant());
    }

    public static JsValue GetLocalName(DomNode node, in JsCall call)
    {
        // localName is null for non-element nodes (text/comment/document).
        if (node is not DomElement element)
            return JsValue.Null;
        if (element.TagName.StartsWith('#'))
            return JsValue.Null; // #comment, #document, etc.
        var name = element.TagName;
        var colonIdx = name.IndexOf(':');
        if (colonIdx >= 0)
            name = name[(colonIdx + 1)..];
        return JsValue.String(name.ToLowerInvariant());
    }

    public static JsValue GetPrefix(DomNode node, in JsCall call)
    {
        if (node is not DomElement element)
            return JsValue.Null;
        var colonIdx = element.TagName.IndexOf(':');
        if (colonIdx >= 0)
            return JsValue.String(element.TagName[..colonIdx]);
        return JsValue.Null;
    }

    public static JsValue GetNamespaceURI(DomNode node, in JsCall call)
    {
        // namespaceURI is null for non-element nodes (text/comment/document).
        if (node is not DomElement element)
            return JsValue.Null;
        if (element.NamespaceUri != null)
            return JsValue.String(element.NamespaceUri);
        // Default namespace for HTML elements
        if (!element.TagName.StartsWith('#'))
            return JsValue.String("http://www.w3.org/1999/xhtml");
        return JsValue.Null;
    }

    public static JsValue GetNodeValue(DomNode node, in JsCall call)
    {
        if (DomBridge.IsText(node) || DomBridge.IsComment(node))
            return JsValue.String(DomBridge.BridgeText(node));
        return JsValue.Null;
    }

    public static JsValue SetNodeValue(INodeAccessorsHost host, DomNode node, in JsCall call)
    {
        // ToJsString, not the handle's rendering: assigning an object here must run that object's own
        // toString, which is the coercion a page observes and the engine's own `ToString()` performed.
        if (DomBridge.IsText(node) || DomBridge.IsComment(node))
            host.SetCharacterData(node, call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty);
        return JsValue.Undefined;
    }

    public static JsValue GetPublicId(DomNode node, in JsCall call) =>
        JsValue.String(node is DomDocumentType dt ? dt.PublicId : string.Empty);

    public static JsValue GetSystemId(DomNode node, in JsCall call) =>
        JsValue.String(node is DomDocumentType dt ? dt.SystemId : string.Empty);

    public static JsValue GetOwnerDocument(INodeAccessorsHost host, DomNode node, in JsCall call)
    {
        // DOM §4.4: a document's own `ownerDocument` is null — it is the node document, not a node
        // that has one. The walk below would hand back the document itself, which is what
        // `document.ownerDocument` answered; Chromium answers null.
        if (node is Broiler.Dom.DomDocument)
            return JsValue.Null;

        // Phase 4 item 1 (P4.4c): the owning document is derived from the canonical tree (connected
        // nodes) or the node's canonical OwnerDocument (detached), not a parallel OwnerDocRoot field.
        var owner = DomBridge.GetOwningDocument(node);
        // A sub-document maps to its JS document wrapper; the main document maps to the window
        // document object.
        if (!ReferenceEquals(owner, host.DocumentNode) && host.TryGetDocumentWrapper(owner, out var subDoc))
            return subDoc;
        return host.DocumentWrapper;
    }

    public static JsValue GetParentElement(INodeAccessorsHost host, DomNode node, in JsCall call)
    {
        var parent = DomBridge.ParentEl(node);
        if (parent == null)
            return JsValue.Null;
        if (DomBridge.IsText(parent))
            return JsValue.Null;
        return host.WrapNode(parent);
    }
}
