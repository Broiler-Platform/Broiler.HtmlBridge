using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The document-level factories exposed on <c>document.implementation</c> —
/// <c>createDocumentType</c>, <c>createDocument</c>, <c>createHTMLDocument</c> — co-located as an
/// HtmlBridge feature module, completing the factory surface begun in
/// <see cref="DocumentFactoryBinding"/>. Each constructs a canonical DOM node/tree through the
/// <see cref="IDocumentLevelFactoryHost"/> funnels (a browsing-context <c>DomDocument</c> root for the
/// two document factories) and returns its JS wrapper. Name validation is asked of the host (it raises
/// the <c>DOMException</c> against the realm's own <c>DOMException</c> constructor) and the neutral
/// tree helper <c>SetParent</c> is the bridge's <c>internal static</c> helper, called directly.
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine
/// type.
/// </remarks>
internal static class DocumentLevelFactoryBinding
{
    public static JsValue CreateDocumentType(IDocumentLevelFactoryHost host, in JsCall call)
    {
        if (call.Length < 3)
        {
            // A plain Error, not a TypeError: WebIDL's arity check is the only thing this reports.
            throw call.Realm.Error(
                JsErrorKind.Error,
                "Failed to execute 'createDocumentType' on 'DOMImplementation': 3 arguments required.");
        }

        var qualifiedName = call.Realm.ToJsString(call[0]);
        var publicId = call.Realm.ToJsString(call[1]);
        var systemId = call.Realm.ToJsString(call[2]);

        // Doctype names with colons are validated as qualified names (NamespaceError if malformed).
        if (qualifiedName.Contains(':'))
            host.ValidateQualifiedName(qualifiedName, null);
        else
            host.ValidateElementName(qualifiedName);

        var doctype = host.CreateBridgeDocumentType(qualifiedName, publicId, systemId);
        return host.ToJsObject(doctype);
    }

    public static JsValue CreateDocument(IDocumentLevelFactoryHost host, in JsCall call)
    {
        var ns = call.Length > 0 && !call[0].IsNullish ? call.Realm.ToJsString(call[0]) : null;
        var qName = call.Length > 1 && !call[1].IsNullish ? call.Realm.ToJsString(call[1]) : null;

        // Missing when fewer than three arguments were passed, and Missing is not an object — the
        // same answer the `a.Length > 2 ? a[2] : null` guard gave, without a CLR null to carry.
        var doctypeArg = call[2];
        if (!string.IsNullOrEmpty(qName))
            host.ValidateQualifiedName(qName, ns);

        // A createDocument root is a canonical DomDocument.
        var docRoot = host.CreateBrowsingContextDocument();

        // Append the doctype if provided — a DocumentType is a legitimate canonical child of a
        // DomDocument. (Reverse-lookup of the argument's wrapper to its canonical node.)
        if (doctypeArg.IsObject && host.FindDomNode(doctypeArg) is { } dtNode)
            docRoot.AppendChild(dtNode);

        // Create the document element if qualifiedName is provided (appended after the doctype, per DOM).
        if (!string.IsNullOrEmpty(qName))
        {
            var docEl = string.IsNullOrEmpty(ns)
                ? host.CreateBridgeElement(qName)
                : host.CreateBridgeElementNS(ns, qName);
            docRoot.AppendChild(docEl);
        }

        return host.BuildDocument(docRoot);
    }

    public static JsValue CreateHTMLDocument(IDocumentLevelFactoryHost host, in JsCall call)
    {
        var title = call.Length > 0 && !call[0].IsNullish ? call.Realm.ToJsString(call[0]) : null;

        // A createHTMLDocument root is a canonical DomDocument; doctype
        // + <html> are appended as canonical document children, so the owner derives from tree position.
        var docRoot = host.CreateBrowsingContextDocument();
        var doctype = host.CreateBridgeDocumentType("html", string.Empty, string.Empty);
        docRoot.AppendChild(doctype);

        // The default HTML namespace ("http://www.w3.org/1999/xhtml") is applied by the funnel.
        var htmlEl = host.CreateBridgeElement("html");
        docRoot.AppendChild(htmlEl);
        var headEl = host.CreateBridgeElement("head");
        htmlEl.AppendChild(headEl);

        // Add a <title> element if a title argument is provided.
        if (title != null)
        {
            var titleEl = host.CreateBridgeElement("title");
            headEl.AppendChild(titleEl);
            var titleText = host.CreateBridgeTextNode(title);
            titleEl.AppendChild(titleText);
        }

        var bodyEl = host.CreateBridgeElement("body");
        htmlEl.AppendChild(bodyEl);
        return host.BuildDocument(docRoot);
    }
}
