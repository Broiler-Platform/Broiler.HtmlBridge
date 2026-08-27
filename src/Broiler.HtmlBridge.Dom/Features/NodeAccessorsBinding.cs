using Broiler.Dom;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.BuiltIns.Array;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Phase 3 feature module for the DOM <c>Node</c> read accessors shared by every node wrapper —
/// <c>isConnected</c>, <c>childNodes</c>, <c>firstChild</c>/<c>lastChild</c>,
/// <c>nextSibling</c>/<c>previousSibling</c>, <c>nodeType</c>/<c>nodeName</c>, <c>localName</c>/
/// <c>prefix</c>/<c>namespaceURI</c>, <c>nodeValue</c> (get/set), <c>publicId</c>/<c>systemId</c>
/// (DocumentType), <c>ownerDocument</c> and <c>parentElement</c>. These were the bridge's
/// <c>JsJsObjectsGet…032</c>..<c>058</c> callbacks; the JS-wrapper factory, the document node, the
/// tree-root walk, the notifying character-data setter and the document-wrapper lookups reach the
/// bridge through <see cref="INodeAccessorsHost"/>, while node-type tests, tree-order helpers, text
/// reads and the owning-document derivation are the bridge's <c>internal static</c> helpers.
/// </summary>
internal static class NodeAccessorsBinding
{
    public static JSValue GetIsConnected(INodeAccessorsHost host, DomNode node, in Arguments _)
    {
        var root = host.GetTreeRoot(node);
        return ReferenceEquals(root, host.DocumentNode) ? JSBoolean.True : JSBoolean.False;
    }

    /// <summary>
    /// <c>node.childNodes</c> — a <b>live</b> <c>NodeList</c> (DOM §4.4). It used to be a plain
    /// array, which is a snapshot: <c>var kids = el.childNodes; el.appendChild(x); kids.length</c>
    /// grew in a browser and did not here, silently answering a stale number rather than failing.
    /// The list holds the walk rather than its result, so every read sees the tree as it is now.
    /// </summary>
    public static JSValue GetChildNodes(INodeAccessorsHost host, DomNode node, in Arguments a) =>
        DomCollectionBinding.NodeList(host.JsContext, () =>
        {
            var children = new List<JSValue>();
            foreach (var child in node.ChildNodes)
                children.Add(host.ToJSObject(child));

            return children;
        });

    public static JSValue GetFirstChild(INodeAccessorsHost host, DomNode node, in Arguments a)
    {
        var first = node.ChildNodes.FirstOrDefault();
        return first != null ? host.ToJSObject(first) : JSNull.Value;
    }

    public static JSValue GetLastChild(INodeAccessorsHost host, DomNode node, in Arguments a)
    {
        var last = node.ChildNodes.LastOrDefault();
        return last != null ? host.ToJSObject(last) : JSNull.Value;
    }

    public static JSValue GetNextSibling(INodeAccessorsHost host, DomNode node, in Arguments a)
    {
        var parent = node.ParentNode;
        if (parent == null)
            return JSNull.Value;
        var siblings = parent.ChildNodes;
        var idx = DomBridge.ChildIndexOf(parent, node);
        return idx >= 0 && idx + 1 < siblings.Count ? host.ToJSObject(siblings[idx + 1]) : JSNull.Value;
    }

    public static JSValue GetPreviousSibling(INodeAccessorsHost host, DomNode node, in Arguments a)
    {
        var parent = node.ParentNode;
        if (parent == null)
            return JSNull.Value;
        var siblings = parent.ChildNodes;
        var idx = DomBridge.ChildIndexOf(parent, node);
        return idx - 1 >= 0 ? host.ToJSObject(siblings[idx - 1]) : JSNull.Value;
    }

    public static JSValue GetNodeType(DomNode node, in Arguments a)
        // The canonical DomNodeType enum values ARE the DOM node-type constants
        // (Element=1, Text=3, Comment=8, Document=9, DocumentType=10, DocumentFragment=11).
        => new JSNumber((int)node.NodeType);

    public static JSValue GetNodeName(DomNode node, in Arguments a)
    {
        if (DomBridge.IsText(node))
            return new JSString("#text");
        if (DomBridge.IsComment(node))
            return new JSString("#comment");
        if (node is DomDocumentType docType)
            return new JSString(docType.Name); // doctype nodeName is its (already lowercased) name
        if (node is DomDocumentFragment)
            return new JSString("#document-fragment");
        if (node is DomDocument)
            return new JSString("#document"); // canonical DomDocument — the document root
        if (node is not DomElement element)
            return JSNull.Value;

        // Non-HTML namespace elements preserve original case (per DOM spec)
        if (!string.IsNullOrEmpty(element.NamespaceUri) && !string.Equals(element.NamespaceUri, "http://www.w3.org/1999/xhtml", StringComparison.OrdinalIgnoreCase))
            return new JSString(element.TagName);
        return new JSString(element.TagName.ToUpperInvariant());
    }

    public static JSValue GetLocalName(DomNode node, in Arguments a)
    {
        // localName is null for non-element nodes (text/comment/document).
        if (node is not DomElement element)
            return JSNull.Value;
        if (element.TagName.StartsWith('#'))
            return JSNull.Value; // #comment, #document, etc.
        var name = element.TagName;
        var colonIdx = name.IndexOf(':');
        if (colonIdx >= 0)
            name = name[(colonIdx + 1)..];
        return new JSString(name.ToLowerInvariant());
    }

    public static JSValue GetPrefix(DomNode node, in Arguments a)
    {
        if (node is not DomElement element)
            return JSNull.Value;
        var colonIdx = element.TagName.IndexOf(':');
        if (colonIdx >= 0)
            return new JSString(element.TagName[..colonIdx]);
        return JSNull.Value;
    }

    public static JSValue GetNamespaceURI(DomNode node, in Arguments a)
    {
        // namespaceURI is null for non-element nodes (text/comment/document).
        if (node is not DomElement element)
            return JSNull.Value;
        if (element.NamespaceUri != null)
            return new JSString(element.NamespaceUri);
        // Default namespace for HTML elements
        if (!element.TagName.StartsWith('#'))
            return new JSString("http://www.w3.org/1999/xhtml");
        return JSNull.Value;
    }

    public static JSValue GetNodeValue(DomNode node, in Arguments a)
    {
        if (DomBridge.IsText(node) || DomBridge.IsComment(node))
            return new JSString(DomBridge.BridgeText(node));
        return JSNull.Value;
    }

    public static JSValue SetNodeValue(INodeAccessorsHost host, DomNode node, in Arguments a)
    {
        if (DomBridge.IsText(node) || DomBridge.IsComment(node))
            host.SetCharacterData(node, a.Length > 0 ? a[0].ToString() : string.Empty);
        return JSUndefined.Value;
    }

    public static JSValue GetPublicId(DomNode node, in Arguments _) =>
        new JSString(node is DomDocumentType dt ? dt.PublicId : string.Empty);

    public static JSValue GetSystemId(DomNode node, in Arguments _) =>
        new JSString(node is DomDocumentType dt ? dt.SystemId : string.Empty);

    public static JSValue GetOwnerDocument(INodeAccessorsHost host, DomNode node, in Arguments a)
    {
        // DOM §4.4: a document's own `ownerDocument` is null — it is the node document, not a node
        // that has one. The walk below would hand back the document itself, which is what
        // `document.ownerDocument` answered; Chromium answers null.
        if (node is Broiler.Dom.DomDocument)
            return JSNull.Value;

        // Phase 4 item 1 (P4.4c): the owning document is derived from the canonical tree (connected
        // nodes) or the node's canonical OwnerDocument (detached), not a parallel OwnerDocRoot field.
        var owner = DomBridge.GetOwningDocument(node);
        // A sub-document maps to its JS document wrapper; the main document maps to the window
        // document object.
        if (!ReferenceEquals(owner, host.DocumentNode) && host.TryGetDocumentWrapper(owner, out var subDoc))
            return subDoc;
        return host.DocumentJSObject ?? JSNull.Value;
    }

    public static JSValue GetParentElement(INodeAccessorsHost host, DomNode node, in Arguments a)
    {
        var parent = DomBridge.ParentEl(node);
        if (parent == null)
            return JSNull.Value;
        if (DomBridge.IsText(parent))
            return JSNull.Value;
        return host.ToJSObject(parent);
    }
}
