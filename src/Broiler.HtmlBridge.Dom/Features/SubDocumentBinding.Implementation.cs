using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <see cref="SubDocumentBinding"/> — the <c>document.implementation</c> factories
/// (<c>createDocumentType</c>/<c>createDocument</c>/<c>createHTMLDocument</c>) and the sub-document's
/// <c>createTreeWalker</c>/<c>createNodeIterator</c>. The created documents are canonical
/// <see cref="DomDocument"/> browsing-context roots (P4.4a) wrapped by <see cref="BuildDocument"/>.
/// </summary>
internal sealed partial class SubDocumentBinding
{
    private JSValue CreateDocumentType(in Arguments a)
    {
        if (a.Length < 3)
            throw new JSException("Failed to execute 'createDocumentType' on 'DOMImplementation': 3 arguments required.");
        var qualifiedName = a[0].ToString();
        var publicId = a[1].ToString();
        var systemId = a[2].ToString();
        DomBridge.ValidateElementName(qualifiedName, _host.JsContext);
        var dt = _host.CreateDocumentType(qualifiedName, publicId, systemId);
        return _host.ToJSObject(dt);
    }

    private JSValue CreateDocument(in Arguments a)
    {
        var ns = a.Length > 0 && !a[0].IsNull && !a[0].IsUndefined ? a[0].ToString() : null;
        var qName = a.Length > 1 && !a[1].IsNull && !a[1].IsUndefined ? a[1].ToString() : null;
        var doctypeArg = a.Length > 2 ? a[2] : null;
        if (!string.IsNullOrEmpty(qName))
            DomBridge.ValidateQualifiedName(qName, ns, _host.JsContext);
        // Phase 4 item 1 (P4.4a): a createDocument root is a canonical DomDocument (was a #subdoc-root).
        // Phase 4 item 1 (P4.4c): structural nodes are appended under subDocRoot (a canonical
        // DomDocument), so GetOwningDocument derives their owner from tree position — no OwnerDocRoot.
        var subDocRoot = _host.CreateBrowsingContextDocument();
        if (doctypeArg is JSObject dtObj && _host.FindDomNodeByJSObject(dtObj) is { } dtNode)
            subDocRoot.AppendChild(dtNode);

        if (!string.IsNullOrEmpty(qName))
        {
            var docEl = string.IsNullOrEmpty(ns)
                ? _host.CreateElement(qName)
                : _host.CreateElementNS(ns, qName);
            subDocRoot.AppendChild(docEl);
        }

        return BuildDocument(subDocRoot);
    }

    private JSValue CreateHTMLDocument(in Arguments a)
    {
        var subTitle = a.Length > 0 && !a[0].IsNull && !a[0].IsUndefined ? a[0].ToString() : null;
        // Phase 4 item 1 (P4.4a): a createHTMLDocument root is a canonical DomDocument (was a
        // #subdoc-root); doctype + <html> are appended as canonical document children.
        // Phase 4 item 1 (P4.4c): structural nodes are appended under subDocRoot (a canonical
        // DomDocument), so GetOwningDocument derives their owner from tree position — no OwnerDocRoot.
        var subDocRoot = _host.CreateBrowsingContextDocument();
        var dt = _host.CreateDocumentType("html", string.Empty, string.Empty);
        subDocRoot.AppendChild(dt);
        // "http://www.w3.org/1999/xhtml" is the default HTML namespace the funnel applies.
        var subHtml = _host.CreateElement("html");
        subDocRoot.AppendChild(subHtml);
        var subHead = _host.CreateElement("head");
        DomBridge.SetParent(subHead, subHtml);
        subHtml.AppendChild(subHead);
        if (subTitle != null)
        {
            var subTitleEl = _host.CreateElement("title");
            DomBridge.SetParent(subTitleEl, subHead);
            subHead.AppendChild(subTitleEl);
            var subTitleText = _host.CreateTextNode(subTitle);
            DomBridge.SetParent(subTitleText, subTitleEl);
            subTitleEl.AppendChild(subTitleText);
        }

        var subBody = _host.CreateElement("body");
        DomBridge.SetParent(subBody, subHtml);
        subHtml.AppendChild(subBody);
        return BuildDocument(subDocRoot);
    }

    private JSValue CreateTreeWalker(in Arguments a)
    {
        if (a.Length == 0)
            throw new JSException("Failed to execute 'createTreeWalker': 1 argument required.");
        if (a[0] is not JSObject rootObj)
            throw new JSException("Failed to execute 'createTreeWalker': parameter 1 is not of type 'Node'.");
        var rootEl = _host.FindDomElementByJSObject(rootObj);
        if (rootEl == null)
            return JSNull.Value;
        var whatToShow = a.Length > 1 && !a[1].IsNull && !a[1].IsUndefined ? unchecked((int)(uint)a[1].DoubleValue) : unchecked((int)0xFFFFFFFF);
        var filterFn = a.Length > 2 && a[2] is JSFunction f ? f : (a.Length > 2 && a[2] is JSObject filterObj ? filterObj[(KeyString)"acceptNode"] as JSFunction : null);
        return _host.BuildTreeWalker(rootEl, whatToShow, filterFn);
    }

    private JSValue CreateNodeIterator(in Arguments a)
    {
        if (a.Length == 0)
            throw new JSException("Failed to execute 'createNodeIterator': 1 argument required.");
        if (a[0] is not JSObject rootObj)
            throw new JSException("Failed to execute 'createNodeIterator': parameter 1 is not of type 'Node'.");
        var rootEl = _host.FindDomElementByJSObject(rootObj);
        if (rootEl == null)
            return JSNull.Value;
        var whatToShow = a.Length > 1 && !a[1].IsNull && !a[1].IsUndefined ? unchecked((int)(uint)a[1].DoubleValue) : unchecked((int)0xFFFFFFFF);
        var filterFn = a.Length > 2 && a[2] is JSFunction f ? f : (a.Length > 2 && a[2] is JSObject filterObj ? filterObj[(KeyString)"acceptNode"] as JSFunction : null);
        return _host.BuildNodeIterator(rootEl, whatToShow, filterFn);
    }
}
