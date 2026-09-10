using Broiler.HtmlBridge.Jseal;
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
/// <remarks>
/// <para>
/// <b>This file named no engine type, and a paragraph here said it did.</b> It listed two things
/// "on the other side of the seam" as the reason: the two reverse wrapper lookups, and the two name
/// validations plus the selector check "each of which raises its <c>DOMException</c> against the
/// script context". Neither survives inspection — the lookups take a JSEAL handle, the three
/// validations raise against the realm rather than against any script context (two take it as an
/// argument and <c>ValidateSelector</c> reads the field), and the engine reference it claimed was
/// never one the ratchet could see, because what stood here was a <c>JsInterop</c> crossing and a
/// crossing names no engine type.
/// </para>
/// <para>
/// Wrapper identity (<c>el === el</c>, and the weak tables keyed on it) is the same question it was
/// before, because <c>Runtime/JsObjectRegistry</c> keys on <see cref="JsValue.ObjectIdentity"/> — the
/// reference the handle carries — rather than on an engine object.
/// </para>
/// <para>
/// Everything this file forwards is now a plain forward: the wrapper factory (<c>WrapNode</c>) answers
/// a handle, <c>DomCollectionBinding</c> and <c>DocumentCollectionBinding</c> both mint into a realm,
/// <c>StartSubDocumentViewTransition</c> takes the one argument it reads rather than an engine frame
/// around it, and the two reverse lookups take the handle their callers already held.
/// </para>
/// </remarks>
public sealed partial class DomBridge : ISubDocumentHost
{
    IJsRealm ISubDocumentHost.Realm => Realm;

    // Missing rather than undefined for "there is no window yet": the module tests it with IsObject,
    // and the value is never handed to script — the null check it replaces guarded the same thing.
    // That is exactly what the bridge's own WindowHandle answers, so this is the sibling handle
    // (DomBridge.cs) rather than a second reading of the engine-typed field.
    JsValue ISubDocumentHost.MainWindow => WindowHandle;

    // The bridge's wrapper factory answers a handle now (DomBridge/JsObjects.cs), so this forwards
    // rather than unwrapping the engine-typed adapter beside it and re-wrapping the result.
    JsValue ISubDocumentHost.ToJsObject(DomNode node) => WrapNode(node);

    void ISubDocumentHost.LinkToInterface(JsValue wrapper, string interfaceName) =>
        LinkToInterface(wrapper, interfaceName);

    bool ISubDocumentHost.NodeInterfacePrototypesReady => _nodeInterfacePrototypesReady;

    // Plain forwards: both reverse lookups take the same handle. There is no unwrap left to fail, and
    // a handle that is not an object answers null — which every caller in SubDocumentBinding still
    // means these never have to do, because each of them guards with IsObject first.
    DomElement? ISubDocumentHost.FindElement(JsValue wrapper) =>
        FindDomElementByJSObject(wrapper);

    DomNode? ISubDocumentHost.FindNode(JsValue wrapper) =>
        FindDomNodeByJSObject(wrapper);

    void ISubDocumentHost.RegisterDocumentWrapper(DomNode docRoot, JsValue doc)
    {
        _jsObjects.SetDocument(docRoot, doc);
        // Map docRoot → the same document wrapper, so the node-wrapper factory hands that object back
        // for the root; this makes strict equality checks like `range.startContainer === doc` work.
        _jsObjects.Set(docRoot, doc);
    }

    bool ISubDocumentHost.TryGetNodeWrapper(DomNode node, out JsValue wrapper)
    {
        if (_jsObjects.TryGet(node, out var cached))
        {
            wrapper = cached;
            return true;
        }

        wrapper = JsValue.Missing;
        return false;
    }

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

    // The three validations raise their DOMException against the realm, through IJsCalls.DomError.
    // They took a script context until the validators did. The selector check is the one that
    // tolerates having no realm — it is a no-op before attach, as it always has been.
    void ISubDocumentHost.ValidateElementName(string name) => ValidateElementName(name, Realm);

    void ISubDocumentHost.ValidateQualifiedName(string qualifiedName, string? ns) =>
        ValidateQualifiedName(qualifiedName, ns, Realm);

    void ISubDocumentHost.ValidateSelector(string selector) => ValidateSelector(selector);

    // The three collection seams are now straight forwards: both builders mint in a realm and speak
    // in handles, so the wrapper list the module produces is the list the collection holds and the
    // named getter it supplies is the one the collection consults. The round trip that used to sit
    // here — every wrapper down to the engine's own value on the way in and back up on the way out,
    // and the collection itself narrowed on the way back — existed only because the builders took a
    // script context, and it is what made a non-object member throw rather than answer.
    JsValue ISubDocumentHost.NodeList(Func<List<JsValue>> contents) =>
        Dom.Features.DomCollectionBinding.NodeList(Realm, contents);

    JsValue ISubDocumentHost.HtmlCollection(Func<List<JsValue>> contents, Func<string, JsValue?>? namedLookup) =>
        Dom.Features.DomCollectionBinding.HtmlCollection(Realm, contents, namedLookup);

    JsValue ISubDocumentHost.DocumentCollection(IDocumentCollectionHost collections, DocumentCollectionKind kind) =>
        kind switch
        {
            DocumentCollectionKind.Forms => Dom.Features.DocumentCollectionBinding.Forms(collections),
            DocumentCollectionKind.Images => Dom.Features.DocumentCollectionBinding.Images(collections),
            DocumentCollectionKind.Links => Dom.Features.DocumentCollectionBinding.Links(collections),
            DocumentCollectionKind.Anchors => Dom.Features.DocumentCollectionBinding.Anchors(collections),
            DocumentCollectionKind.Scripts => Dom.Features.DocumentCollectionBinding.Scripts(collections),
            DocumentCollectionKind.StyleSheets => Dom.Features.DocumentCollectionBinding.StyleSheets(collections),
            _ => Dom.Features.DocumentCollectionBinding.Embeds(collections),
        };

    void ISubDocumentHost.SetElementTextContent(DomElement element, string? value) => SetElementTextContent(element, value);
    IReadOnlyList<DomElement> ISubDocumentHost.HitTestDocumentPoint(DomNode docRoot, double x, double y) =>
        HitTestDocumentPoint(docRoot, x, y);

    JsValue ISubDocumentHost.BuildStyleSheetObject(DomElement styleElement) =>
        BuildStyleSheet(styleElement);

    bool ISubDocumentHost.HasAssociatedStyleSheet(DomElement element) => HasAssociatedStyleSheet(element);

    // The traversal module is migrated, so these three reach it directly rather than through the
    // engine-typed wrappers in DomBridge/Traversal.cs that existed for this caller alone.
    JsValue ISubDocumentHost.BuildRange(DomNode docRoot) => _traversal.BuildRange(docRoot);

    JsValue ISubDocumentHost.GetSelection(DomNode docRoot) => _traversal.SelectionObject(docRoot);

    JsValue ISubDocumentHost.BuildTreeWalker(DomElement root, int whatToShow, JsValue filter) =>
        _traversal.BuildTreeWalker(root, whatToShow, filter);

    JsValue ISubDocumentHost.BuildNodeIterator(DomElement root, int whatToShow, JsValue filter) =>
        _traversal.BuildNodeIterator(root, whatToShow, filter);

    bool ISubDocumentHost.MatchesSelector(DomElement element, string selector, DomElement? scope) =>
        MatchesSelector(element, selector, scope);

    /// <summary>
    /// <c>append</c>/<c>prepend</c>'s argument list as canonical nodes.
    /// </summary>
    /// <remarks>
    /// The same reading the bridge's own <c>BuildChildNodeArgumentNodes</c> performs, over a migrated
    /// call frame rather than an engine one: a wrapper contributes its node (a
    /// <c>DocumentFragment</c> contributes its children, per DOM), and everything else — a string, a
    /// number, an object that is no node — is coerced and minted as a text node. The coercion is the
    /// realm's <c>ToString</c>, because that is what the engine-typed <c>value.ToString()</c> it
    /// replaces performed: an object argument runs its own <c>toString</c>. The two readings become
    /// one again when the other node-mutation contracts migrate their frames.
    /// </remarks>
    List<DomNode> ISubDocumentHost.BuildChildNodeArgumentNodes(ReadOnlySpan<JsValue> arguments)
    {
        var nodes = new List<DomNode>();
        foreach (var value in arguments)
        {
            if (value.IsObject &&
                FindDomNodeByJSObject(value) is { } candidateNode)
            {
                if (candidateNode is DomDocumentFragment candidateFragment)
                {
                    foreach (var fragmentChild in candidateFragment.ChildNodes.ToArray())
                        nodes.Add(fragmentChild);
                    continue;
                }

                nodes.Add(candidateNode);
                continue;
            }

            nodes.Add(CreateBridgeTextNode(Realm.ToJsString(value)));
        }

        return nodes;
    }

    void ISubDocumentHost.InsertNodeAt(DomNode parent, DomNode node, int index) => InsertNodeAt(parent, node, index);
    void ISubDocumentHost.NotifyNodeIteratorPreRemoval(DomNode node) => NotifyNodeIteratorPreRemoval(node);
    void ISubDocumentHost.NotifyChildRemoved(DomElement parent, DomNode removedChild, int index) =>
        NotifyChildRemoved(parent, removedChild, index);

    /// <summary>
    /// <c>startViewTransition()</c> on the sub-document, over the one argument the operation takes.
    /// </summary>
    /// <remarks>
    /// The implementation (<c>DomBridge.ViewTransition.SubDocument.cs</c>) takes the handle now, so
    /// this is a forward. It used to mint a one-slot engine argument frame around the same value,
    /// because that implementation read a frame and read exactly one slot of it — the update callback,
    /// or the options object carrying it. The frame is not missed: a primitive and an omitted argument
    /// both reach the same "no update callback" arm there that they reached through it.
    /// </remarks>
    JsValue ISubDocumentHost.StartViewTransition(DomNode docRoot, JsValue options) =>
        StartSubDocumentViewTransition(docRoot, options);
}
