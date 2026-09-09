using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <see cref="SubDocumentBinding"/> — the <c>document.implementation</c> factories
/// (<c>createDocumentType</c>/<c>createDocument</c>/<c>createHTMLDocument</c>) and the sub-document's
/// <c>createTreeWalker</c>/<c>createNodeIterator</c>. The created documents are canonical
/// <see cref="DomDocument"/> browsing-context roots (P4.4a) wrapped by <see cref="Build"/>.
/// </summary>
internal sealed partial class SubDocumentBinding
{
    private JsValue CreateDocumentType(in JsCall call)
    {
        if (call.Length < 3)
        {
            // A plain Error, not a TypeError: that is what the engine-typed `new JSException(message)`
            // this replaces constructed, and WebIDL's arity check is the only thing it reports.
            throw call.Realm.Error(
                JsErrorKind.Error,
                "Failed to execute 'createDocumentType' on 'DOMImplementation': 3 arguments required.");
        }

        var qualifiedName = call.Realm.ToJsString(call[0]);
        var publicId = call.Realm.ToJsString(call[1]);
        var systemId = call.Realm.ToJsString(call[2]);
        _host.ValidateElementName(qualifiedName);
        var dt = _host.CreateDocumentType(qualifiedName, publicId, systemId);
        return _host.ToJsObject(dt);
    }

    private JsValue CreateDocument(in JsCall call)
    {
        var ns = call.Length > 0 && !call[0].IsNullish ? call.Realm.ToJsString(call[0]) : null;
        var qName = call.Length > 1 && !call[1].IsNullish ? call.Realm.ToJsString(call[1]) : null;

        // Missing when fewer than three arguments were passed, and Missing is not an object — the
        // same answer the `a.Length > 2 ? a[2] : null` guard gave, without a CLR null to carry.
        var doctypeArg = call[2];
        if (!string.IsNullOrEmpty(qName))
            _host.ValidateQualifiedName(qName, ns);
        // Phase 4 item 1 (P4.4a): a createDocument root is a canonical DomDocument (was a #subdoc-root).
        // Phase 4 item 1 (P4.4c): structural nodes are appended under subDocRoot (a canonical
        // DomDocument), so GetOwningDocument derives their owner from tree position — no OwnerDocRoot.
        var subDocRoot = _host.CreateBrowsingContextDocument();
        if (doctypeArg.IsObject && _host.FindNode(doctypeArg) is { } dtNode)
            subDocRoot.AppendChild(dtNode);

        if (!string.IsNullOrEmpty(qName))
        {
            var docEl = string.IsNullOrEmpty(ns)
                ? _host.CreateElement(qName)
                : _host.CreateElementNS(ns, qName);
            subDocRoot.AppendChild(docEl);
        }

        return Build(subDocRoot);
    }

    private JsValue CreateHTMLDocument(in JsCall call)
    {
        var subTitle = call.Length > 0 && !call[0].IsNullish ? call.Realm.ToJsString(call[0]) : null;
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
        return Build(subDocRoot);
    }

    private JsValue CreateTreeWalker(in JsCall call)
    {
        if (call.Length == 0)
            throw call.Realm.Error(JsErrorKind.Error, "Failed to execute 'createTreeWalker': 1 argument required.");
        if (!call[0].IsObject)
            throw call.Realm.Error(JsErrorKind.Error, "Failed to execute 'createTreeWalker': parameter 1 is not of type 'Node'.");
        var rootEl = _host.FindElement(call[0]);
        if (rootEl == null)
            return JsValue.Null;
        return _host.BuildTreeWalker(rootEl, WhatToShowArgument(in call), FilterArgument(in call));
    }

    private JsValue CreateNodeIterator(in JsCall call)
    {
        if (call.Length == 0)
            throw call.Realm.Error(JsErrorKind.Error, "Failed to execute 'createNodeIterator': 1 argument required.");
        if (!call[0].IsObject)
            throw call.Realm.Error(JsErrorKind.Error, "Failed to execute 'createNodeIterator': parameter 1 is not of type 'Node'.");
        var rootEl = _host.FindElement(call[0]);
        if (rootEl == null)
            return JsValue.Null;
        return _host.BuildNodeIterator(rootEl, WhatToShowArgument(in call), FilterArgument(in call));
    }

    /// <summary>
    /// The <c>whatToShow</c> bitmask: absent, <c>null</c> or <c>undefined</c> means SHOW_ALL. The
    /// conversion is the realm's <c>ToNumber</c>, because a page may pass the mask as a string and the
    /// engine's own numeric view of an argument — which this site read directly before — is that
    /// coercion.
    /// </summary>
    private static int WhatToShowArgument(in JsCall call) =>
        call.Length > 1 && !call[1].IsNullish
            ? unchecked((int)(uint)call.Realm.ToNumber(call[1]))
            : unchecked((int)0xFFFFFFFF);

    /// <summary>
    /// The <c>NodeFilter</c>: a bare function, or the <c>acceptNode</c> of a filter object, or
    /// nothing.
    /// </summary>
    /// <remarks>
    /// <see cref="JsValue.Missing"/> is "no filter", which is what the traversal module tests
    /// callability against — the same question the null this replaces asked. An <c>acceptNode</c> that
    /// is not callable is no filter either, which is what the cast to the engine's function type said.
    /// </remarks>
    private static JsValue FilterArgument(in JsCall call)
    {
        if (call.Length <= 2)
            return JsValue.Missing;
        if (call[2].IsFunction)
            return call[2];
        if (!call[2].IsObject)
            return JsValue.Missing;

        var acceptNode = call.Realm.GetProperty(call[2], "acceptNode");
        return acceptNode.IsFunction ? acceptNode : JsValue.Missing;
    }
}
