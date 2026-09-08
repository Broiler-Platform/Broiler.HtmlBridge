using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <see cref="SubDocumentBinding"/> — node construction (<c>createElement</c>/<c>createTextNode</c>/
/// <c>createComment</c>/<c>createElementNS</c>), <c>open</c>/<c>write</c>, and the child-mutation
/// methods (<c>removeChild</c>/<c>appendChild</c>/<c>append</c>/<c>prepend</c>) on the sub-document.
/// </summary>
internal sealed partial class SubDocumentBinding
{
    private JsValue CreateElement(DomNode docRoot, in JsCall call)
    {
        if (call.Length == 0)
        {
            // A plain Error, not a TypeError: it is what the engine-typed `new JSException(message)`
            // this replaces constructed, and the arity failure is all it reports.
            throw call.Realm.Error(JsErrorKind.Error, "Failed to execute 'createElement': 1 argument required.");
        }

        var tagName = call.Realm.ToJsString(call[0]);
        _host.ValidateElementName(tagName);
        tagName = DomBridge.AsciiToLower(tagName);
        var el = _host.CreateElement(tagName);
        _host.AdoptDetachedNode(el, docRoot);
        return _host.ToJsObject(el);
    }

    /// <summary>
    /// <c>document.adoptNode(node)</c> on a sub-document — moves the node itself into this document,
    /// which is what a custom element in the moved subtree hears as <c>adoptedCallback</c>.
    /// </summary>
    private JsValue AdoptNode(DomNode docRoot, in JsCall call)
    {
        if (call.Length == 0 || !call[0].IsObject || _host.FindNode(call[0]) is not { } node)
            throw call.Realm.DomError("TypeError", "adoptNode requires a node to adopt.");

        if (node is DomDocument)
        {
            throw call.Realm.DomError(
                "NotSupportedError",
                "Failed to execute 'adoptNode' on 'Document': The node provided is of type '#document', which may not be adopted.");
        }

        _host.AdoptDetachedNode(node, docRoot);
        return _host.ToJsObject(node);
    }

    private JsValue CreateTextNode(DomNode docRoot, in JsCall call)
    {
        var text = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
        var el = _host.CreateTextNode(text);
        _host.AdoptDetachedNode(el, docRoot);
        return _host.ToJsObject(el);
    }

    private JsValue CreateComment(DomNode docRoot, in JsCall call)
    {
        var data = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
        var el = _host.CreateComment(data);
        _host.AdoptDetachedNode(el, docRoot);
        return _host.ToJsObject(el);
    }

    private JsValue CreateElementNS(DomNode docRoot, in JsCall call)
    {
        var ns = call.Length > 0 && !call[0].IsNullish ? call.Realm.ToJsString(call[0]) : null;
        var localName = call.Length > 1
            ? call.Realm.ToJsString(call[1])
            : (call.Length > 0 ? call.Realm.ToJsString(call[0]) : "div");
        _host.ValidateQualifiedName(localName, ns);
        var el = string.IsNullOrEmpty(ns)
            ? _host.CreateElement(localName)
            : _host.CreateElementNS(ns, localName);
        _host.AdoptDetachedNode(el, docRoot);
        return _host.ToJsObject(el);
    }

    private static JsValue Open(JsValue doc, DomNode docRoot)
    {
        DomBridge.ClearChildren(docRoot);
        return doc.IsObject ? doc : JsValue.Null;
    }

    private JsValue Write(DomNode docRoot, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Undefined;
        var fragment = call.Realm.ToJsString(call[0]);
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

        return JsValue.Undefined;
    }

    private JsValue RemoveChild(DomNode docRoot, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Null;
        if (!call[0].IsObject)
            return JsValue.Null;
        var childObj = call[0];
        foreach (var child in DomBridge.ChildElements(docRoot).ToList())
        {
            // Wrapper identity, not node identity: two handles compare equal when they carry the same
            // underlying object, which is the same reference test this used to perform directly.
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

    private JsValue AppendChild(DomNode docRoot, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Null;
        if (!call[0].IsObject)
            return call[0];
        var childObj = call[0];
        // Phase 4 item 1: match any DomNode so a canonical DomDocumentType / DomDocumentFragment can be
        // appended to a sub-document root (was `is DomElement`, which skipped them).
        if (_host.FindNode(childObj) is { } child)
        {
            if (DomBridge.ParentEl(child) != null)
                child.Remove();
            DomBridge.SetParent(child, docRoot);
            docRoot.AppendChild(child);
            return childObj;
        }

        return childObj;
    }

    private JsValue Append(DomNode docRoot, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Undefined;
        var nodes = _host.BuildChildNodeArgumentNodes(call.Arguments);
        var insertIndex = docRoot.ChildNodes.Count;
        foreach (var node in nodes)
            _host.InsertNodeAt(docRoot, node, insertIndex++);
        return JsValue.Undefined;
    }

    private JsValue Prepend(DomNode docRoot, in JsCall call)
    {
        if (call.Length == 0)
            return JsValue.Undefined;
        var nodes = _host.BuildChildNodeArgumentNodes(call.Arguments);
        var insertIndex = 0;
        foreach (var node in nodes)
            _host.InsertNodeAt(docRoot, node, insertIndex++);
        return JsValue.Undefined;
    }
}
