using Broiler.JavaScript.Engine;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="ISubDocumentHost"/>, the contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.SubDocumentBinding"/> feature module consumes
/// (HtmlBridge complexity-reduction roadmap Phase 3, P3.13). Explicit interface members, so these seams
/// do not widen the public <c>DomBridge</c> surface. The bridge keeps the browsing-context
/// infrastructure (the sub-document/-window caches, the content-document maps, resource loading and
/// onload) — the module owns only the <c>document</c> object surface built over a root node.
/// </summary>
public sealed partial class DomBridge : ISubDocumentHost
{
    JSContext ISubDocumentHost.JsContext => _jsContext!;
    JSObject? ISubDocumentHost.WindowJSObject => _windowJSObject;

    JSObject ISubDocumentHost.ToJSObject(DomNode node) => ToJSObject(node);
    void ISubDocumentHost.LinkToInterface(JSObject wrapper, string interfaceName) => LinkToInterface(wrapper, interfaceName);
    bool ISubDocumentHost.NodeInterfacePrototypesReady => _nodeInterfacePrototypesReady;
    DomElement? ISubDocumentHost.FindDomElementByJSObject(JSObject jsObj) => FindDomElementByJSObject(jsObj);
    DomNode? ISubDocumentHost.FindDomNodeByJSObject(JSObject jsObj) => FindDomNodeByJSObject(jsObj);

    void ISubDocumentHost.RegisterDocumentWrapper(DomNode docRoot, JSObject doc)
    {
        _jsObjects.SetDocument(docRoot, doc);
        // Map docRoot → doc JSObject so ToJSObject(docRoot) returns the doc object; this makes strict
        // equality checks like `range.startContainer === doc` work.
        _jsObjects.Set(docRoot, doc);
    }

    bool ISubDocumentHost.TryGetNodeWrapper(DomNode node, out JSObject wrapper) => _jsObjects.TryGet(node, out wrapper);

    // Phase 4 item 1 (P4.4c): a sub-document createElement/… node is minted from the main _document
    // and returned detached, so its canonical OwnerDocument would be the main document. Adopt it into
    // the sub-document's content DomDocument (a no-op RemoveChild since it is parentless) so
    // GetOwningDocument's detached fallback (node.OwnerDocument) reports the sub-document.
    void ISubDocumentHost.AdoptDetachedNode(DomNode node, DomNode docRoot)
    {
        if (docRoot is DomDocument document)
            document.AdoptNode(node);
    }

    DomElement ISubDocumentHost.CreateElement(string tagName) => CreateBridgeElement(tagName);
    DomElement ISubDocumentHost.CreateElementNS(string ns, string localName) => CreateBridgeElementNS(ns, localName);
    DomText ISubDocumentHost.CreateTextNode(string data) => CreateBridgeTextNode(data);
    DomComment ISubDocumentHost.CreateComment(string data) => CreateBridgeCommentNode(data);
    DomDocumentType ISubDocumentHost.CreateDocumentType(string name, string publicId, string systemId) =>
        CreateBridgeDocumentType(name, publicId, systemId);
    DomDocument ISubDocumentHost.CreateBrowsingContextDocument() => CreateBrowsingContextDocument();
    DomDocumentType? ISubDocumentHost.ParseDocType(string html) => ParseDocType(html);

    void ISubDocumentHost.SetElementTextContent(DomElement element, string? value) => SetElementTextContent(element, value);
    IReadOnlyList<DomElement> ISubDocumentHost.HitTestDocumentPoint(DomNode docRoot, double x, double y) =>
        HitTestDocumentPoint(docRoot, x, y);
    JSObject ISubDocumentHost.BuildStyleSheetObject(DomElement styleElement) => BuildStyleSheetObject(styleElement);
    bool ISubDocumentHost.HasAssociatedStyleSheet(DomElement element) => HasAssociatedStyleSheet(element);
    JSObject ISubDocumentHost.BuildRange(DomNode docRoot) => BuildRange(docRoot);
    JSValue ISubDocumentHost.GetSelection(DomNode docRoot) => _traversal.GetSelection(docRoot);
    JSObject ISubDocumentHost.BuildTreeWalker(DomElement root, int whatToShow, JSFunction? filterFn) =>
        BuildTreeWalker(root, whatToShow, filterFn);
    JSObject ISubDocumentHost.BuildNodeIterator(DomElement root, int whatToShow, JSFunction? filterFn) =>
        BuildNodeIterator(root, whatToShow, filterFn);
    bool ISubDocumentHost.MatchesSelector(DomElement element, string selector, DomElement? scope) =>
        MatchesSelector(element, selector, scope);

    List<DomNode> ISubDocumentHost.BuildChildNodeArgumentNodes(in Arguments arguments) =>
        BuildChildNodeArgumentNodes(arguments);
    void ISubDocumentHost.InsertNodeAt(DomNode parent, DomNode node, int index) => InsertNodeAt(parent, node, index);
    void ISubDocumentHost.NotifyNodeIteratorPreRemoval(DomNode node) => NotifyNodeIteratorPreRemoval(node);
    void ISubDocumentHost.NotifyChildRemoved(DomElement parent, DomNode removedChild, int index) =>
        NotifyChildRemoved(parent, removedChild, index);

    JSValue ISubDocumentHost.StartViewTransition(DomNode docRoot, in Arguments arguments) =>
        StartSubDocumentViewTransition(docRoot, in arguments);
}
