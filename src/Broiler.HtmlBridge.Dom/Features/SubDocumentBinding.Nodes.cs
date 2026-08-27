using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <see cref="SubDocumentBinding"/> — node construction (<c>createElement</c>/<c>createTextNode</c>/
/// <c>createComment</c>/<c>createElementNS</c>), <c>open</c>/<c>write</c>, and the child-mutation
/// methods (<c>removeChild</c>/<c>appendChild</c>/<c>append</c>/<c>prepend</c>) on the sub-document.
/// </summary>
internal sealed partial class SubDocumentBinding
{
    private JSValue CreateElement(DomNode docRoot, in Arguments a)
    {
        if (a.Length == 0)
            throw new JSException("Failed to execute 'createElement': 1 argument required.");
        var tagName = a[0].ToString();
        DomBridge.ValidateElementName(tagName, _host.JsContext);
        tagName = DomBridge.AsciiToLower(tagName);
        var el = _host.CreateElement(tagName);
        _host.AdoptDetachedNode(el, docRoot);
        return _host.ToJSObject(el);
    }

    /// <summary>
    /// <c>document.adoptNode(node)</c> on a sub-document — moves the node itself into this document,
    /// which is what a custom element in the moved subtree hears as <c>adoptedCallback</c>.
    /// </summary>
    private JSValue AdoptNode(DomNode docRoot, in Arguments a)
    {
        if (a.Length == 0 || a[0] is not JSObject source || _host.FindDomNodeByJSObject(source) is not { } node)
        {
            DomBridge.ThrowDOMException(_host.JsContext, "adoptNode requires a node to adopt.", "TypeError");
            return JSUndefined.Value;
        }

        if (node is DomDocument)
        {
            DomBridge.ThrowDOMException(
                _host.JsContext,
                "Failed to execute 'adoptNode' on 'Document': The node provided is of type '#document', which may not be adopted.",
                "NotSupportedError");
            return JSUndefined.Value;
        }

        _host.AdoptDetachedNode(node, docRoot);
        return _host.ToJSObject(node);
    }

    private JSValue CreateTextNode(DomNode docRoot, in Arguments a)
    {
        var text = a.Length > 0 ? a[0].ToString() : string.Empty;
        var el = _host.CreateTextNode(text);
        _host.AdoptDetachedNode(el, docRoot);
        return _host.ToJSObject(el);
    }

    private JSValue CreateComment(DomNode docRoot, in Arguments a)
    {
        var data = a.Length > 0 ? a[0].ToString() : string.Empty;
        var el = _host.CreateComment(data);
        _host.AdoptDetachedNode(el, docRoot);
        return _host.ToJSObject(el);
    }

    private JSValue CreateElementNS(DomNode docRoot, in Arguments a)
    {
        var ns = a.Length > 0 && !a[0].IsNull && !a[0].IsUndefined ? a[0].ToString() : null;
        var localName = a.Length > 1 ? a[1].ToString() : (a.Length > 0 ? a[0].ToString() : "div");
        DomBridge.ValidateQualifiedName(localName, ns, _host.JsContext);
        var el = string.IsNullOrEmpty(ns)
            ? _host.CreateElement(localName)
            : _host.CreateElementNS(ns, localName);
        _host.AdoptDetachedNode(el, docRoot);
        return _host.ToJSObject(el);
    }

    private static JSValue Open(JSObject? doc, DomNode docRoot)
    {
        DomBridge.ClearChildren(docRoot);
        return doc ?? (JSValue)JSNull.Value;
    }

    private JSValue Write(DomNode docRoot, in Arguments a)
    {
        if (a.Length == 0)
            return JSUndefined.Value;
        var fragment = a[0].ToString();
        // Parse DOCTYPE if present
        var doctype = _host.ParseDocType(fragment);
        var (parsedDoc, _, _, _) = DomBridge.BuildDocumentTree(fragment);
        if (docRoot.ChildNodes.Count == 0)
        {
            if (doctype != null)
            {
                DomBridge.SetParent(doctype, docRoot);
                docRoot.AppendChild(doctype);
            }

            // parsedDoc is the <html> element from HtmlTreeBuilder.
            // Add it directly to docRoot (not its children).
            DomBridge.SetParent(parsedDoc, docRoot);
            docRoot.AppendChild(parsedDoc);
        }
        else
        {
            var bodyEl = DomBridge.FindInSubTree(docRoot, el => string.Equals(el.TagName, "body", StringComparison.OrdinalIgnoreCase));
            if (bodyEl != null)
            {
                var parsedBody = DomBridge.FindInTree(parsedDoc, el => string.Equals(el.TagName, "body", StringComparison.OrdinalIgnoreCase));
                if (parsedBody != null)
                {
                    foreach (var child in parsedBody.ChildNodes.ToArray())
                    {
                        DomBridge.SetParent(child, bodyEl);
                        bodyEl.AppendChild(child);
                    }
                }
            }
        }

        return JSUndefined.Value;
    }

    private JSValue RemoveChild(DomNode docRoot, in Arguments a)
    {
        if (a.Length == 0)
            return JSNull.Value;
        if (a[0] is not JSObject childObj)
            return JSNull.Value;
        foreach (var child in DomBridge.ChildElements(docRoot).ToList())
        {
            if (_host.TryGetNodeWrapper(child, out var cached) && cached == childObj)
            {
                var idx = DomBridge.ChildIndexOf(docRoot, child);
                if (idx >= 0)
                {
                    _host.NotifyNodeIteratorPreRemoval(child);
                    DomBridge.RemoveNthChild(docRoot, idx);
                    DomBridge.SetParent(child, null);
                    // Phase 4 item 1 (P4.4a): child-list mutation-observer notification is element-only;
                    // a canonical DomDocument browsing-context root (regime-B) has no such observers.
                    if (docRoot is DomElement docRootElement)
                        _host.NotifyChildRemoved(docRootElement, child, idx);
                }

                return childObj;
            }
        }

        return childObj;
    }

    private JSValue AppendChild(DomNode docRoot, in Arguments a)
    {
        if (a.Length == 0)
            return JSNull.Value;
        if (a[0] is not JSObject childObj)
            return a.Length > 0 ? a[0] : JSNull.Value;
        // Phase 4 item 1: match any DomNode so a canonical DomDocumentType / DomDocumentFragment can be
        // appended to a sub-document root (was `is DomElement`, which skipped them).
        if (_host.FindDomNodeByJSObject(childObj) is { } child)
        {
            if (DomBridge.ParentEl(child) != null)
                child.Remove();
            DomBridge.SetParent(child, docRoot);
            docRoot.AppendChild(child);
            return childObj;
        }

        return a[0];
    }

    private JSValue Append(DomNode docRoot, in Arguments a)
    {
        if (a.Length == 0)
            return JSUndefined.Value;
        var nodes = _host.BuildChildNodeArgumentNodes(a);
        var insertIndex = docRoot.ChildNodes.Count;
        foreach (var node in nodes)
            _host.InsertNodeAt(docRoot, node, insertIndex++);
        return JSUndefined.Value;
    }

    private JSValue Prepend(DomNode docRoot, in Arguments a)
    {
        if (a.Length == 0)
            return JSUndefined.Value;
        var nodes = _host.BuildChildNodeArgumentNodes(a);
        var insertIndex = 0;
        foreach (var node in nodes)
            _host.InsertNodeAt(docRoot, node, insertIndex++);
        return JSUndefined.Value;
    }
}
