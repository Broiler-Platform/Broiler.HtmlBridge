using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Engine;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The document-level factories exposed on <c>document.implementation</c> —
/// <c>createDocumentType</c>, <c>createDocument</c>, <c>createHTMLDocument</c> — co-located as an
/// HtmlBridge feature module (Phase 3), completing the factory surface begun in
/// <see cref="DocumentFactoryBinding"/> (P3.25). Each constructs a canonical DOM node/tree through the
/// <see cref="IDocumentLevelFactoryHost"/> funnels (a browsing-context <c>DomDocument</c> root for the
/// two document factories) and returns its JS wrapper. Name validation and the neutral tree helper
/// <c>SetParent</c> are the bridge's <c>internal static</c> helpers, called directly. Previously the
/// bridge's <c>JsRegistrationCreateDocumentType057Core</c>..<c>CreateHTMLDocument059Core</c> in the
/// shared JsFunctionCallbacks/Registration.cs grab-bag.
/// </summary>
internal static class DocumentLevelFactoryBinding
{
    public static JSValue CreateDocumentType(IDocumentLevelFactoryHost host, JSContext context, in Arguments a)
    {
        if (a.Length < 3)
            throw new JSException("Failed to execute 'createDocumentType' on 'DOMImplementation': 3 arguments required.");
        var qualifiedName = a[0].ToString();
        var publicId = a[1].ToString();
        var systemId = a[2].ToString();
        // Doctype names with colons are validated as qualified names (NamespaceError if malformed).
        if (qualifiedName.Contains(':'))
            DomBridge.ValidateQualifiedName(qualifiedName, null, context);
        else
            DomBridge.ValidateElementName(qualifiedName, context);
        var doctype = host.CreateBridgeDocumentType(qualifiedName, publicId, systemId);
        return host.ToJSObject(doctype);
    }

    public static JSValue CreateDocument(IDocumentLevelFactoryHost host, JSContext context, in Arguments a)
    {
        var ns = a.Length > 0 && !a[0].IsNull && !a[0].IsUndefined ? a[0].ToString() : null;
        var qName = a.Length > 1 && !a[1].IsNull && !a[1].IsUndefined ? a[1].ToString() : null;
        var doctypeArg = a.Length > 2 ? a[2] : null;
        if (!string.IsNullOrEmpty(qName))
            DomBridge.ValidateQualifiedName(qName, ns, context);

        // A createDocument root is a canonical DomDocument (Phase 4 item 1 / P4.4a).
        var docRoot = host.CreateBrowsingContextDocument();

        // Append the doctype if provided — a DocumentType is a legitimate canonical child of a
        // DomDocument. (Reverse-lookup of the argument's wrapper to its canonical node.)
        if (doctypeArg is JSObject dtObj && host.FindDomNodeByJSObject(dtObj) is { } dtNode)
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

    public static JSValue CreateHTMLDocument(IDocumentLevelFactoryHost host, in Arguments a)
    {
        var title = a.Length > 0 && !a[0].IsNull && !a[0].IsUndefined ? a[0].ToString() : null;
        // A createHTMLDocument root is a canonical DomDocument (Phase 4 item 1 / P4.4a/P4.4c); doctype
        // + <html> are appended as canonical document children, so the owner derives from tree position.
        var docRoot = host.CreateBrowsingContextDocument();
        var doctype = host.CreateBridgeDocumentType("html", string.Empty, string.Empty);
        docRoot.AppendChild(doctype);
        // The default HTML namespace ("http://www.w3.org/1999/xhtml") is applied by the funnel.
        var htmlEl = host.CreateBridgeElement("html");
        docRoot.AppendChild(htmlEl);
        var headEl = host.CreateBridgeElement("head");
        DomBridge.SetParent(headEl, htmlEl);
        htmlEl.AppendChild(headEl);
        // Add a <title> element if a title argument is provided.
        if (title != null)
        {
            var titleEl = host.CreateBridgeElement("title");
            DomBridge.SetParent(titleEl, headEl);
            headEl.AppendChild(titleEl);
            var titleText = host.CreateBridgeTextNode(title);
            DomBridge.SetParent(titleText, titleEl);
            titleEl.AppendChild(titleText);
        }

        var bodyEl = host.CreateBridgeElement("body");
        DomBridge.SetParent(bodyEl, htmlEl);
        htmlEl.AppendChild(bodyEl);
        return host.BuildDocument(docRoot);
    }
}
