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
/// The contract above is spelled in JSEAL; this file is where that meets the half of the bridge that is
/// still engine-typed. <see cref="Dom.Runtime.JsInterop"/> is the cast between them and not a
/// conversion — a JSEAL object handle carries the engine's own object — so wrapper identity
/// (<c>el === el</c>, and the weak tables keyed on it) is the same question it was before.
/// </para>
/// <para>
/// Five things on the other side of the seam are unmigrated and are why this file names engine types
/// at all: the wrapper factory and its reverse lookup, the two name validations and the selector check
/// (each raises its <c>DOMException</c> against the script context), <c>DomCollectionBinding</c> and
/// <c>DocumentCollectionBinding</c> (which build their collections over engine values), and
/// <c>StartSubDocumentViewTransition</c> (which still takes an engine argument frame). They are
/// unwrapped here and nowhere above.
/// </para>
/// </remarks>
public sealed partial class DomBridge : ISubDocumentHost
{
    IJsRealm ISubDocumentHost.Realm => Realm;

    // Missing rather than undefined for "there is no window yet": the module tests it with IsObject,
    // and the value is never handed to script — the null check it replaces guarded the same thing.
    JsValue ISubDocumentHost.MainWindow =>
        _windowJSObject is { } window ? Dom.Runtime.JsInterop.FromEngineObject(window) : JsValue.Missing;

    JsValue ISubDocumentHost.ToJsObject(DomNode node) =>
        Dom.Runtime.JsInterop.FromEngineObject(ToJSObject(node));

    void ISubDocumentHost.LinkToInterface(JsValue wrapper, string interfaceName) =>
        LinkToInterface(Dom.Runtime.JsInterop.ToEngineObject(wrapper), interfaceName);

    bool ISubDocumentHost.NodeInterfacePrototypesReady => _nodeInterfacePrototypesReady;

    // The module only asks these of a handle it has already established is an object, so unwrapping
    // cannot fail here; a non-object would mean the module skipped its own guard.
    DomElement? ISubDocumentHost.FindElement(JsValue wrapper) =>
        FindDomElementByJSObject(Dom.Runtime.JsInterop.ToEngineObject(wrapper));

    DomNode? ISubDocumentHost.FindNode(JsValue wrapper) =>
        FindDomNodeByJSObject(Dom.Runtime.JsInterop.ToEngineObject(wrapper));

    void ISubDocumentHost.RegisterDocumentWrapper(DomNode docRoot, JsValue doc)
    {
        var wrapper = Dom.Runtime.JsInterop.ToEngineObject(doc);
        _jsObjects.SetDocument(docRoot, wrapper);
        // Map docRoot → doc JSObject so ToJSObject(docRoot) returns the doc object; this makes strict
        // equality checks like `range.startContainer === doc` work.
        _jsObjects.Set(docRoot, wrapper);
    }

    bool ISubDocumentHost.TryGetNodeWrapper(DomNode node, out JsValue wrapper)
    {
        if (_jsObjects.TryGet(node, out var cached))
        {
            wrapper = Dom.Runtime.JsInterop.FromEngineObject(cached);
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

    // The three validations raise their DOMException against the script context, which is what the
    // module used to be handed so that it could pass it straight back here. The selector check is the
    // one that tolerates a null context — it is a no-op before attach, as it always has been.
    void ISubDocumentHost.ValidateElementName(string name) => ValidateElementName(name, _jsContext!);

    void ISubDocumentHost.ValidateQualifiedName(string qualifiedName, string? ns) =>
        ValidateQualifiedName(qualifiedName, ns, _jsContext!);

    void ISubDocumentHost.ValidateSelector(string selector) => ValidateSelector(selector, _jsContext);

    JsValue ISubDocumentHost.NodeList(Func<List<JsValue>> contents) =>
        AdoptSubDocumentCollection(Dom.Features.DomCollectionBinding.NodeList(
            _jsContext, () => ToSubDocumentCollectionItems(contents())));

    JsValue ISubDocumentHost.HtmlCollection(Func<List<JsValue>> contents, Func<string, JsValue?>? namedLookup) =>
        AdoptSubDocumentCollection(Dom.Features.DomCollectionBinding.HtmlCollection(
            _jsContext,
            () => ToSubDocumentCollectionItems(contents()),
            namedLookup is null
                ? null
                : name => namedLookup(name) is { } named ? Dom.Runtime.JsInterop.ToEngineValue(named) : null));

    JsValue ISubDocumentHost.DocumentCollection(IDocumentCollectionHost collections, DocumentCollectionKind kind) =>
        AdoptSubDocumentCollection(kind switch
        {
            DocumentCollectionKind.Forms => Dom.Features.DocumentCollectionBinding.Forms(collections, _jsContext),
            DocumentCollectionKind.Images => Dom.Features.DocumentCollectionBinding.Images(collections, _jsContext),
            DocumentCollectionKind.Links => Dom.Features.DocumentCollectionBinding.Links(collections, _jsContext),
            DocumentCollectionKind.Anchors => Dom.Features.DocumentCollectionBinding.Anchors(collections, _jsContext),
            DocumentCollectionKind.Scripts => Dom.Features.DocumentCollectionBinding.Scripts(collections, _jsContext),
            DocumentCollectionKind.StyleSheets => Dom.Features.DocumentCollectionBinding.StyleSheets(collections, _jsContext),
            _ => Dom.Features.DocumentCollectionBinding.Embeds(collections, _jsContext),
        });

    /// <summary>The module's wrapper handles as the engine values a collection stores.</summary>
    private static List<JavaScript.Runtime.JSValue> ToSubDocumentCollectionItems(List<JsValue> values)
    {
        var engineValues = new List<JavaScript.Runtime.JSValue>(values.Count);
        foreach (var value in values)
        {
            // Every member of a sub-document collection is a node wrapper, so the cast cannot fail; a
            // primitive would mean the module produced something a collection cannot hold.
            engineValues.Add(Dom.Runtime.JsInterop.ToEngineObject(value));
        }

        return engineValues;
    }

    /// <summary>A handle over a collection the unmigrated collection builders produced.</summary>
    /// <remarks>
    /// Their static type is <c>JSValue</c> and their dynamic type is always the collection object —
    /// each builder has one return statement — so the pattern is a type-narrowing rather than a branch
    /// expected to fall through. The fallback is <c>undefined</c> rather than a throw because a
    /// collection getter answering an empty-ish value is a better outcome for a page than an exception
    /// thrown from inside a property read.
    /// </remarks>
    private static JsValue AdoptSubDocumentCollection(JavaScript.Runtime.JSValue collection) =>
        collection is JavaScript.Runtime.JSObject @object
            ? Dom.Runtime.JsInterop.FromEngineObject(@object)
            : JsValue.Undefined;

    void ISubDocumentHost.SetElementTextContent(DomElement element, string? value) => SetElementTextContent(element, value);
    IReadOnlyList<DomElement> ISubDocumentHost.HitTestDocumentPoint(DomNode docRoot, double x, double y) =>
        HitTestDocumentPoint(docRoot, x, y);

    JsValue ISubDocumentHost.BuildStyleSheetObject(DomElement styleElement) =>
        Dom.Runtime.JsInterop.FromEngineObject(BuildStyleSheetObject(styleElement));

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
                FindDomNodeByJSObject(Dom.Runtime.JsInterop.ToEngineObject(value)) is { } candidateNode)
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
    /// The implementation (<c>DomBridge.ViewTransition.SubDocument.cs</c>) still reads an engine
    /// argument frame, and reads exactly one slot of it — the update callback, or the options object
    /// carrying it — so a one-slot frame over this handle is the whole of what it can observe. A
    /// handle carrying a primitive has no engine value to unwrap and stands in as <c>undefined</c>,
    /// which that implementation ignores exactly as it ignored the primitive it replaces; and an
    /// omitted argument arrives here as <see cref="JsValue.Missing"/>, which takes the same path,
    /// since the only thing the frame's length decides there is whether to look at slot zero at all.
    /// </remarks>
    JsValue ISubDocumentHost.StartViewTransition(DomNode docRoot, JsValue options)
    {
        var frame = new JavaScript.Runtime.Arguments(
            JavaScript.Runtime.JSUndefined.Value,
            Dom.Runtime.JsInterop.ToEngineValue(options) ?? JavaScript.Runtime.JSUndefined.Value);

        var transition = StartSubDocumentViewTransition(docRoot, in frame);
        return transition is JavaScript.Runtime.JSObject @object
            ? Dom.Runtime.JsInterop.FromEngineObject(@object)
            : JsValue.Undefined;
    }
}
