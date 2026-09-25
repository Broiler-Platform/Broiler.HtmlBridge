using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JSeal;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

// Explicit IDocumentCollectionHost implementation for the DocumentCollectionBinding feature module:
// the bridge exposes its realm, the element list, the JS-wrapper factory and the
// stylesheet-object builder via explicit interface members, so the module never reaches an arbitrary
// bridge private field and the public surface is unchanged.
//
// Explicit, including Realm: DomBridge.Realm is internal, so an implicit implementation of a member
// on an internal interface would be CS0737.
//
// The wrapper factory answers a handle now, so the member below forwards to it. The unqualified name
// resolves to the bridge's own internal WrapNode (DomBridge/JsObjects.cs) and not to this explicit
// implementation, which is reachable only through the interface -- so this is a forward, not a
// recursion. The stylesheet builder answers a handle too, so it is a second such forward: the
// collection is built over the cached sheet objects themselves and sheet identity is untouched.
public sealed partial class DomBridge : Dom.Features.IDocumentCollectionHost
{
    int Dom.Features.IDocumentCollectionHost.CurrentScriptIndex => CurrentScriptIndex;

    DomElement? Dom.Features.IDocumentCollectionHost.RunningInsertedScript => RunningInsertedScript;

    JsValue Dom.Features.IDocumentCollectionHost.BuildStyleSheetObject(DomElement styleElement)
        => BuildStyleSheet(styleElement);

    bool Dom.Features.IDocumentCollectionHost.HasAssociatedStyleSheet(DomElement element)
        => HasAssociatedStyleSheet(element);
}

// Explicit IDocumentEventTargetHost implementation for the DocumentEventTargetBinding feature module:
// the bridge exposes the document node, its per-type listener store and the shared
// event-dispatch algorithm via explicit interface members, so the module never reaches an arbitrary
// bridge private field and the public surface is unchanged.
//
// EventListenerRegistration holds its listener as a handle, and DocumentEventTargetBinding calls
// Features/EventListenerBinding.cs itself.
public sealed partial class DomBridge : Dom.Features.IDocumentEventTargetHost
{
    Dictionary<string, List<EventListenerRegistration>> Dom.Features.IDocumentEventTargetHost.GetEventListeners(DomNode node)
        => GetEventListeners(node);

    // The migrated dispatch answers the "not cancelled" boolean the DOM says dispatchEvent returns,
    // which the engine-typed adapter that stood beside it re-materialised as a JSBoolean.
    JsValue Dom.Features.IDocumentEventTargetHost.DispatchEvent(DomNode target, JsValue evt)
        => JsValue.Boolean(_eventDispatch.DispatchEventOnElement(target, evt).AsBoolean);
}

// Explicit IDocumentFactoryHost implementation for the DocumentFactoryBinding feature module:
// the bridge exposes the node-construction funnels, standalone Attr-node construction,
// the JS-wrapper factory and reverse lookup, and the two name validations via explicit interface
// members, so the module never reaches an arbitrary bridge private field and the public surface is
// unchanged.
//
// The contract is spelled in JSEAL and so is every bridge member behind it: the wrapper factory
// answers a handle and the reverse lookup takes one. A node wrapper the module hands back through
// WrapNode is the handle JsObjectRegistry caches for the node, and the reverse lookup reads the
// registry's reverse table, which keys on JsValue.ObjectIdentity. The Attr that createAttribute hands
// back is not a node wrapper: BuildStandaloneAttrNode mints it and no table caches it.
public sealed partial class DomBridge : Dom.Features.IDocumentFactoryHost
{
    // Missing rather than undefined for "nothing is defined for this name": the module tests it with
    // IsMissing and never hands it to script, which is what the null check it replaces did. The
    // IsObject filter is kept rather than delegating outright, so a non-object answer still reads as
    // "nothing matched" instead of reaching a caller that expects a wrapper.
    JsValue Dom.Features.IDocumentFactoryHost.CreateDefinedCustomElement(string tagName, string? isValue) =>
        CustomElements.CreateDefinedElement(tagName, isValue) is { IsObject: true } upgraded
            ? upgraded
            : JsValue.Missing;

    void Dom.Features.IDocumentFactoryHost.RecordCustomElementIsValue(DomElement element, string isValue) =>
        CustomElements.RecordIsValue(element, isValue);

    DomNode Dom.Features.IDocumentFactoryHost.AdoptNode(DomNode node) => _document.AdoptNode(node);

    DomElement Dom.Features.IDocumentFactoryHost.CreateBridgeElement(string tagName)
        => NoteCreatedByScript(CreateBridgeElement(tagName));

    DomElement Dom.Features.IDocumentFactoryHost.CreateBridgeElementNS(string? namespaceUri, string tagName)
        => NoteCreatedByScript(CreateBridgeElementNS(namespaceUri, tagName));

    /// <summary>
    /// Tells the <see cref="Dom.Runtime.ScriptInsertionRunner"/> that <paramref name="element"/> was
    /// built by <c>document.createElement</c>/<c>createElementNS</c>, which is what makes a
    /// <c>&lt;script&gt;</c> eligible to run when it is inserted. This contract is the JS factory's
    /// only route into the bridge's construction funnels — every other caller reaches the private
    /// methods directly — so marking here marks script-created elements and nothing else: neither
    /// the parser's scripts (already run from the document source) nor an <c>innerHTML</c> parse's
    /// (never run, per spec) pass through it. Returns its argument so it can wrap the call.
    /// </summary>
    private DomElement NoteCreatedByScript(DomElement element)
    {
        _scriptInsertion.NoteCreatedByScript(element);
        return element;
    }

    DomDocumentFragment Dom.Features.IDocumentFactoryHost.CreateBridgeDocumentFragment()
        => CreateBridgeDocumentFragment();

    JsValue Dom.Features.IDocumentFactoryHost.BuildStandaloneAttrNode(string qualifiedName, string? namespaceUri)
        => _attributes.BuildStandaloneAttrNode(qualifiedName, namespaceUri);

    // A plain forward: the reverse lookup takes the same handle. There is no unwrap left to fail, and
    // a handle that is not an object answers null — which the module's own IsObject guard, at both of
    // its call sites, still means this never has to do.
    DomNode? Dom.Features.IDocumentFactoryHost.FindDomNode(JsValue wrapper)
        => FindDomNodeByJSObject(wrapper);

    DomNode Dom.Features.IDocumentFactoryHost.CloneDomNode(DomNode source, bool deep)
        => CloneDomElement(source, deep);
}

// Explicit IDocumentLevelFactoryHost implementation for the DocumentLevelFactoryBinding feature
// module: the bridge exposes the JS-wrapper factory and reverse lookup, the
// node-construction funnels, the browsing-context document-root factory, the sub-document builder and
// the two name validations via explicit interface members, so the module never reaches an arbitrary
// bridge private field and the public surface is unchanged.
//
// The contract is spelled in JSEAL and so is every bridge member behind it: the wrapper factory
// answers a handle and the reverse lookup takes one. The node wrappers the module hands back are the
// handles JsObjectRegistry caches per node, and the reverse lookup reads its reverse table, which
// keys on JsValue.ObjectIdentity.
public sealed partial class DomBridge : Dom.Features.IDocumentLevelFactoryHost
{
    // A plain forward: the reverse lookup takes the same handle. There is no unwrap left to fail, and
    // a handle that is not an object answers null — which the module's own IsObject guard still means
    // this never has to do.
    DomNode? Dom.Features.IDocumentLevelFactoryHost.FindDomNode(JsValue wrapper)
        => FindDomNodeByJSObject(wrapper);

    DomDocumentType Dom.Features.IDocumentLevelFactoryHost.CreateBridgeDocumentType(string name, string publicId, string systemId)
        => CreateBridgeDocumentType(name, publicId, systemId);

    DomElement Dom.Features.IDocumentLevelFactoryHost.CreateBridgeElement(string tagName)
        => CreateBridgeElement(tagName);

    DomElement Dom.Features.IDocumentLevelFactoryHost.CreateBridgeElementNS(string? namespaceUri, string tagName)
        => CreateBridgeElementNS(namespaceUri, tagName);

    DomDocument Dom.Features.IDocumentLevelFactoryHost.CreateBrowsingContextDocument()
        => CreateBrowsingContextDocument();

    // Build answers the handle.
    JsValue Dom.Features.IDocumentLevelFactoryHost.BuildDocument(DomNode docRoot)
        => _subDocuments.Build(docRoot);
}

// Explicit IDocumentQueryHost implementation for the DocumentQueryBinding feature module:
// the bridge exposes the document root, the document-order element list, the JS-wrapper factory,
// selector validation and the two collection factories via explicit interface members, so the module
// never reaches an arbitrary bridge private field and the public surface is unchanged.
//
// The contract above is spelled in JSEAL and so is everything this file forwards to: the wrapper
// factory answers a handle, the collection builder takes the realm and the module's own list, and the
// selector validation raises its DOMException through the realm. Nothing is unwrapped here.
public sealed partial class DomBridge : Dom.Features.IDocumentQueryHost
{
    JsValue Dom.Features.IDocumentQueryHost.NodeList(Func<List<JsValue>> contents) =>
        Dom.Features.DomCollectionBinding.NodeList(Realm, contents);

    JsValue Dom.Features.IDocumentQueryHost.HtmlCollection(
        Func<List<JsValue>> contents, Func<string, JsValue?>? namedLookup) =>
        Dom.Features.DomCollectionBinding.HtmlCollection(Realm, contents, namedLookup);
}

// Explicit IDocumentStructureHost implementation for the DocumentStructureBinding feature module:
// the bridge exposes the document root, the JS-wrapper factory and the document title via
// explicit interface members, so the module never reaches an arbitrary bridge private field and the
// public surface is unchanged.
//
// The contract is spelled in JSEAL and so is the bridge member behind it: ToJsObject forwards to
// WrapNode, which answers the handle the realm minted, and nothing here converts.
public sealed partial class DomBridge : Dom.Features.IDocumentStructureHost
{
    string Dom.Features.IDocumentStructureHost.Title
    {
        get => Title;
        set => Title = value;
    }
}

// Explicit IDocumentWriteHost implementation for the DocumentWriteBinding feature module:
// the bridge exposes the document root, the document-order element list and the current parser
// insertion point via explicit interface members so the module never reaches an arbitrary bridge
// private field and the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IDocumentWriteHost
{
    int Dom.Features.IDocumentWriteHost.CurrentScriptIndex => CurrentScriptIndex;
}

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="ISubDocumentHost"/>, the contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.SubDocumentBinding"/> feature module consumes.
/// Explicit interface members, so these seams
/// do not widen the public <c>DomBridge</c> surface. The bridge keeps the browsing-context
/// infrastructure (the sub-document/-window caches, the content-document maps, resource loading and
/// onload) — the module owns only the <c>document</c> object surface built over a root node.
/// </summary>
/// <remarks>
/// <para>
/// Wrapper identity (<c>el === el</c>, and the weak tables keyed on it) is the same question it was
/// before, because <c>Runtime/JsObjectRegistry</c> keys on <see cref="JsValue.ObjectIdentity"/> — the
/// reference the handle carries — rather than on an engine object.
/// </para>
/// <para>
/// Everything this file forwards is a plain forward: the wrapper factory (<c>WrapNode</c>) answers a
/// handle, <c>DomCollectionBinding</c> and <c>DocumentCollectionBinding</c> both mint into a realm,
/// <c>StartSubDocumentViewTransition</c> takes the one argument it reads, and the two reverse lookups
/// take the handle their callers already held.
/// </para>
/// </remarks>
public sealed partial class DomBridge : ISubDocumentHost
{
    // Missing rather than undefined for "there is no window yet": the module tests it with IsObject,
    // and the value is never handed to script — the null check it replaces guarded the same thing.
    // That is exactly what the bridge's window root holds, so this forwards WindowHandle
    // (DomBridge.cs) as it stands.
    JsValue ISubDocumentHost.MainWindow => WindowHandle;

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

    // A sub-document createElement/… node is minted from the main _document
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

    // The three collection seams are straight forwards: both builders mint in a realm and speak in
    // handles, so the wrapper list the module produces is the list the collection holds and the named
    // getter it supplies is the one the collection consults.
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

    /// <summary>
    /// <c>append</c>/<c>prepend</c>'s argument list as canonical nodes.
    /// </summary>
    /// <remarks>
    /// The bridge's one reading of a child-node argument list: the child-node, node-mutation and
    /// tree-mutation contracts forward here too. A wrapper contributes its node (a
    /// <c>DocumentFragment</c> contributes its children, per DOM), and everything else — a string, a
    /// number, an object that is no node — is coerced and minted as a text node. The coercion is the
    /// realm's <c>ToString</c>, so an object argument runs its own <c>toString</c>.
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

    /// <summary>
    /// <c>startViewTransition()</c> on the sub-document, over the one argument the operation takes.
    /// </summary>
    /// <remarks>
    /// The implementation (<c>DomBridge/ViewTransition.SubDocument.cs</c>) takes the handle, so this
    /// is a plain forward. A primitive and an omitted argument both reach the same "no update
    /// callback" arm there.
    /// </remarks>
    JsValue ISubDocumentHost.StartViewTransition(DomNode docRoot, JsValue options) =>
        StartSubDocumentViewTransition(docRoot, options);
}

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="ISubWindowHost"/>, the contract the extracted
/// <see cref="Broiler.HtmlBridge.Dom.Features.SubWindowBinding"/> feature module consumes. Explicit interface members, so these seams do not widen
/// the public <c>DomBridge</c> surface. The module owns the sub-window object and its scroll/
/// getComputedStyle surface; the bridge keeps the sub-document builder, resource loading and scroll
/// geometry it reaches through here.
/// </summary>
/// <remarks>
/// The contract is spelled in JSEAL and so is every bridge member behind it. <c>MainWindow</c> below
/// forwards the bridge's window root, the handle the realm minted, which took out the last crossing in
/// this file; the sub-document, the computed-style object and the element lookup already forwarded
/// handles. What the module receives is what the bridge's own caches hold, because it is the same
/// handle rather than a second one over the same object.
/// </remarks>
public sealed partial class DomBridge : ISubWindowHost
{
    // Missing rather than undefined for "there is no window yet": the module tests it with IsObject
    // and never hands it to script, which is what the null check it replaces did.
    JsValue ISubWindowHost.MainWindow => WindowHandle;

    bool ISubWindowHost.IsWindowCrossOriginToCurrentScript(JsValue window) => IsWindowCrossOriginToCurrentScript(window);

    DomDocument? ISubWindowHost.GetContentDocument(DomElement container) => GetContentDocument(container);

    DomElement? ISubWindowHost.GetFrameForContentDocument(DomNode? owningDocument) =>
        GetFrameForContentDocument(owningDocument);

    string ISubWindowHost.ResolveSubResourceUrl(string resourceUrl, string? baseUrl) =>
        ResolveSubResourceUrl(resourceUrl, baseUrl);

    string ISubWindowHost.GetInheritedSubDocumentBaseUrl(DomElement container) =>
        GetInheritedSubDocumentBaseUrl(container);

    double ISubWindowHost.GetElementScrollOffset(DomElement element, bool vertical) =>
        GetElementScrollOffset(element, vertical);

    void ISubWindowHost.SetElementScroll(DomElement element, double? left, double? top, bool relative, string? behavior) =>
        SetElementScrollOffsetsWithBehavior(element, left, top, relative: relative, clamp: false, behavior: behavior);

    /// <summary>
    /// <c>scroll(x, y)</c> / <c>scroll({ left, top, behavior })</c>, read off a migrated call frame.
    /// </summary>
    /// <remarks>
    /// The one reading both scroll contracts share — an options object wins over positional
    /// coordinates, an absent or nullish member is "leave this axis alone", and a blank behaviour is
    /// none — over JSEAL values. The coercions are the realm's because
    /// <c>scrollTo("100", "200")</c> is a page passing strings.
    /// <c>DomBridge/Hosts.Window.cs</c> forwards its own contract's member here rather than
    /// keeping a second copy, which is why the member's name has to be changed in both contracts at
    /// once or not at all.
    /// </remarks>
    (double? Left, double? Top, string? Behavior) ISubWindowHost.GetScrollArguments(ReadOnlySpan<JsValue> supplied)
    {
        if (supplied.Length == 0)
            return (null, null, null);

        var realm = Realm;
        if (supplied[0].IsObject)
        {
            var options = supplied[0];
            return (
                ScrollCoordinateOption(realm, options, "left"),
                ScrollCoordinateOption(realm, options, "top"),
                ScrollBehaviorOption(realm, options));
        }

        return (
            realm.ToNumber(supplied[0]),
            supplied.Length > 1 ? realm.ToNumber(supplied[1]) : null,
            null);
    }

    // A plain forward: the reverse lookup takes the same handle. The module guards with IsObject
    // before asking, and a handle that is not an object would answer null rather than throw.
    DomElement? ISubWindowHost.FindElement(JsValue wrapper) =>
        FindDomElementByJSObject(wrapper);

    // The computed-style builder is migrated, so this one hands the handle straight through.
    JsValue ISubWindowHost.BuildComputedStyleObject(DomElement? element, string? pseudoElement) =>
        BuildComputedStyleObject(element, pseudoElement);

    /// <summary>
    /// A global of this realm, for the sub-window's mirror list.
    /// </summary>
    /// <remarks>
    /// The read is the same one <c>_jsContext[name]</c> performed — the global object <em>is</em> the
    /// context under this engine (<see cref="JsCapabilities.GlobalIsVariableScope"/>) — so a name the
    /// realm does not define answers <c>undefined</c> and is mirrored as such. The <see langword="false"/>
    /// result means only that there is no realm at all, which is the case the null-conditional it
    /// replaces was guarding.
    /// </remarks>
    bool ISubWindowHost.TryGetGlobal(string name, out JsValue value)
    {
        if (_realm is not { } realm)
        {
            value = JsValue.Missing;
            return false;
        }

        value = realm.GetProperty(realm.Global, name);
        return true;
    }

    void ISubWindowHost.PublishPendingSubDocumentGlobals(DomElement containerElement, JsValue subWindow) =>
        PublishPendingSubDocumentGlobals(containerElement, subWindow);
}

// Explicit IIframeElementHost implementation for the IframeElementBinding feature module: the
// <iframe> browsing-context accessors reach the frames machinery through this seam — the same-origin gate,
// the sub-document / sub-window factories, and the src/srcdoc reload hooks — forwarding to the existing
// bridge members (the sub-window map and the fired-onload latch live on the BrowsingContextManager /
// SubWindowBinding owners).
//
// Neither half of this seam is engine-typed. The module is written against JSEAL and receives JsValue
// handles, and the two factories below answer the handles the browsing-context caches hold, so
// `frame.contentWindow === frame.contentWindow` compares the handle the cache filed with itself. This
// paragraph called the file the engine-typed half and said both factories still handed back the
// engine's own objects through a cast. The two conversions it described were deleted from this file
// when the sweep re-typed the sub-window and sub-document builders, and the paragraph was not; the
// caches behind the factories hold handles too since the sub-window maps were re-typed.
//
// Realm is implemented explicitly because DomBridge.Realm is internal: an implicit implementation of a
// public interface member cannot be satisfied by a non-public property (CS0737).
public sealed partial class DomBridge : Dom.Features.IIframeElementHost
{
    bool Dom.Features.IIframeElementHost.IsCurrentIframeCrossOrigin(DomElement element) => IsCurrentIframeCrossOrigin(element);

    JsValue Dom.Features.IIframeElementHost.GetOrCreateSubWindow(DomElement element)
        => _subWindows.GetOrCreate(element);

    void Dom.Features.IIframeElementHost.InvalidateCachedSubDocument(DomElement element) => InvalidateCachedSubDocument(element);
    void Dom.Features.IIframeElementHost.ClearOnloadFired(DomElement element) => _browsingContexts.ClearOnloadFired(element);
    void Dom.Features.IIframeElementHost.FireSubDocumentOnload(DomElement element) => FireSubDocumentOnload(element);
}

// Explicit IObjectElementHost implementation for the ObjectElementBinding feature module: the
// <object>-element sub-document accessors reach the browsing-context machinery through this narrow seam —
// the live page URL plus the sub-document invalidation / load-failure / factory hooks — while the neutral
// content-attribute and same-origin helpers are called as internal statics.
//
// Neither half of this seam is engine-typed: the sub-document factory forwards the bridge's own, which
// answers the handle the browsing-context cache holds, so `obj.contentDocument === obj.contentDocument`
// compares that handle with itself.
public sealed partial class DomBridge : Dom.Features.IObjectElementHost
{
    void Dom.Features.IObjectElementHost.InvalidateCachedSubDocument(DomElement containerElement) => InvalidateCachedSubDocument(containerElement);
    bool Dom.Features.IObjectElementHost.IsObjectLoadFailed(DomElement objectElement) => IsObjectLoadFailed(objectElement);
    bool Dom.Features.IObjectElementHost.IsFrameDocumentCrossOrigin(DomElement objectElement) => IsFrameDocumentCrossOrigin(objectElement);
}
