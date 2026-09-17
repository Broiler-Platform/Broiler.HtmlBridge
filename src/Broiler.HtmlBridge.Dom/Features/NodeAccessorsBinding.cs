using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Phase 3 feature module for the DOM <c>Node</c> read accessors shared by every node wrapper —
/// <c>isConnected</c>, <c>childNodes</c>, <c>firstChild</c>/<c>lastChild</c>,
/// <c>nextSibling</c>/<c>previousSibling</c>, <c>nodeType</c>/<c>nodeName</c>, <c>localName</c>/
/// <c>prefix</c>/<c>namespaceURI</c>, <c>nodeValue</c> (get/set), the <c>textContent</c> setter,
/// <c>publicId</c>/<c>systemId</c> (DocumentType), <c>ownerDocument</c> and <c>parentElement</c>. These
/// were the bridge's <c>JsJsObjectsGet…032</c>..<c>058</c> callbacks; the JS-wrapper factory, the live
/// <c>childNodes</c> collection, the document node, the notifying character-data setter and the
/// document-wrapper lookups reach the bridge through <see cref="INodeAccessorsHost"/>,
/// while node-type tests, tree-order helpers, text reads and the owning-document derivation are the
/// bridge's <c>internal static</c> helpers, and <c>textContent</c> is the canonical
/// <see cref="DomNode.TextContent"/>.
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine
/// type. Only two members read an argument at all — <see cref="SetNodeValue"/> and
/// <see cref="SetTextContent"/> — and both ask the realm for the coercion rather than rendering the
/// handle: an object assigned to <c>nodeValue</c> runs its own <c>toString</c>, which is what a page
/// observes and what <c>JsValue.ToString()</c> would not do.
/// </remarks>
internal static class NodeAccessorsBinding
{
    /// <summary>
    /// <c>node.isConnected</c> (DOM §4.4): whether the node is connected, meaning its shadow-including
    /// root is a document (DOM §4.2.2) — the canonical <see cref="DomNode.IsConnected"/>.
    /// </summary>
    /// <remarks>
    /// <b>Any document, not the page's.</b> This compared the node's root with the page's own document,
    /// so every node in a frame's document or in one <c>createHTMLDocument</c> built — that document's
    /// <c>body</c> and <c>documentElement</c> included — answered <c>false</c>, and a framed script's
    /// "am I in the page yet?" guard never opened. A shadow tree needs nothing extra: the bridge parents
    /// its synthetic <c>#shadow-root</c> element into the host, so the plain root walk crosses every
    /// host on the way up, nested shadow trees included, and ends at the outermost host's root — which
    /// is the shadow-including root the Standard asks about.
    /// </remarks>
    public static JsValue GetIsConnected(DomNode node, in JsCall call) =>
        JsValue.Boolean(node.IsConnected);

    /// <summary>
    /// <c>node.childNodes</c> — a <b>live</b> <c>NodeList</c> (DOM §4.4). It used to be a plain
    /// array, which is a snapshot: <c>var kids = el.childNodes; el.appendChild(x); kids.length</c>
    /// grew in a browser and did not here, silently answering a stale number rather than failing.
    /// The list holds the walk rather than its result, so every read sees the tree as it is now.
    /// </summary>
    /// <remarks>
    /// Assembling that collection is <see cref="INodeAccessorsHost.ChildNodeList"/>'s: the bridge
    /// mints the <c>NodeList</c> through <c>DomCollectionBinding</c> in its realm, wrapping the
    /// children on every read. (This said that was engine-typed, and a list would convert twice.)
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
        var idx = DomBridgeUtils.ChildIndexOf(parent, node);
        return idx >= 0 && idx + 1 < siblings.Count ? host.WrapNode(siblings[idx + 1]) : JsValue.Null;
    }

    public static JsValue GetPreviousSibling(INodeAccessorsHost host, DomNode node, in JsCall call)
    {
        var parent = node.ParentNode;
        if (parent == null)
            return JsValue.Null;
        var siblings = parent.ChildNodes;
        var idx = DomBridgeUtils.ChildIndexOf(parent, node);
        return idx - 1 >= 0 ? host.WrapNode(siblings[idx - 1]) : JsValue.Null;
    }

    public static JsValue GetNodeType(DomNode node, in JsCall call)
        // The canonical DomNodeType enum values ARE the DOM node-type constants
        // (Element=1, Text=3, Comment=8, Document=9, DocumentType=10, DocumentFragment=11).
        => JsValue.Number((int)node.NodeType);

    public static JsValue GetNodeName(DomNode node, in JsCall call)
    {
        if (DomBridgeUtils.IsText(node))
            return JsValue.String("#text");
        if (DomBridgeUtils.IsComment(node))
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
        if (DomBridgeUtils.IsText(node) || DomBridgeUtils.IsComment(node))
            return JsValue.String(DomBridgeUtils.BridgeText(node));
        return JsValue.Null;
    }

    public static JsValue SetNodeValue(INodeAccessorsHost host, DomNode node, in JsCall call)
    {
        // ToJsString, not the handle's rendering: assigning an object here must run that object's own
        // toString, which is the coercion a page observes and the engine's own `ToString()` performed.
        if (DomBridgeUtils.IsText(node) || DomBridgeUtils.IsComment(node))
            host.SetCharacterData(node, call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty);
        return JsValue.Undefined;
    }

    /// <summary>
    /// The <c>textContent</c> setter for every node kind: the canonical <see cref="DomNode.TextContent"/>
    /// setter, which sets a character-data node's data, replaces an element's or a fragment's children
    /// with one text node (or none), and ignores a write to a document or a doctype (DOM §4.4).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>textContent</c> is a nullable <c>DOMString</c>, which is why this is not
    /// <see cref="SetNodeValue"/>'s coercion.</b> WebIDL turns JavaScript <c>null</c> and <c>undefined</c>
    /// into IDL null before anything is converted, and the setter treats null as the empty string: a
    /// text node's data becomes <c>""</c> and an element is left with no children. Coercing first, as
    /// every <c>textContent</c> setter here used to, wrote the string <c>"null"</c> instead.
    /// </para>
    /// <para>
    /// The replace-all mints its text node from the node's own document and publishes <em>one</em>
    /// child-list record carrying every removed child and the added text node, with null siblings; see
    /// <c>MutationObserverBinding.OnDocumentMutation</c> for how that record reaches script.
    /// </para>
    /// </remarks>
    public static JsValue SetTextContent(DomNode node, in JsCall call)
    {
        node.TextContent = call.Length > 0 && !call[0].IsNullish ? call.Realm.ToJsString(call[0]) : null;
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
        var owner = DomBridgeUtils.GetOwningDocument(node);
        // A sub-document maps to its JS document wrapper; the main document maps to the window
        // document object.
        if (!ReferenceEquals(owner, host.DocumentNode) && host.TryGetDocumentWrapper(owner, out var subDoc))
            return subDoc;
        return host.DocumentWrapper;
    }

    public static JsValue GetParentElement(INodeAccessorsHost host, DomNode node, in JsCall call)
    {
        var parent = DomBridgeUtils.ParentEl(node);
        if (parent == null)
            return JsValue.Null;
        if (DomBridgeUtils.IsText(parent))
            return JsValue.Null;
        return host.WrapNode(parent);
    }
}
