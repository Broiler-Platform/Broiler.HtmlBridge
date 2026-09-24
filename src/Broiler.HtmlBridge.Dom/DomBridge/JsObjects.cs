using Broiler.CSS;
using Broiler.Dom;
using Broiler.JSeal;
using Broiler.HtmlBridge.Logging;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// The node-wrapper hub: it turns a <see cref="DomNode"/> into the JavaScript object a page holds for
/// it, dispatching by node kind and then installing — or, where an interface prototype carries them,
/// deliberately not installing — that kind's whole member surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>One wrapper factory, and it answers a handle.</b>
/// <see cref="WrapNode"/> is the JSEAL-vocabulary entry point and the implementation: the object
/// is minted by <see cref="IJsValues.NewObject"/> — or by <see cref="IJsValues.NewExotic"/> for a
/// <c>&lt;form&gt;</c> — and all of its members are installed through the realm.
/// Wrapper identity lives in <c>Runtime/JsObjectRegistry.cs</c>, whose wrapper-to-node table keys on
/// <see cref="JsValue.ObjectIdentity"/>.
/// </para>
/// <para>
/// Four host contracts name this operation <c>ToJsObject</c> (<c>IDocumentLevelFactoryHost</c>,
/// <c>IDocumentQueryHost</c>, <c>IDocumentStructureHost</c>, <c>ISubDocumentHost</c>); each forwards here.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    // The JS-object registry is keyed by canonical DomNode, so the DomText/DomComment nodes
    // construction creates (which get JS wrappers) round-trip. Wrapper identity lives in
    // JsObjectRegistry, the single authority.
    private readonly Dom.Runtime.JsObjectRegistry _jsObjects = new();
    /// <summary>Counter for tracking top-layer insertion order via showModal().</summary>
    private int _topLayerCounter;

    /// <summary>
    /// A node's JS wrapper as a JSEAL handle, answered from <c>JsObjectRegistry</c> while the node stays
    /// registered and minted here otherwise; the four host contracts' <c>ToJsObject</c> forward here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The same object, whichever name reaches it.</b> A wrapper reached through here and one
    /// reached through a host contract's <c>ToJsObject</c> are the same handle over the same object, so
    /// <c>el === el</c> holds, and <c>JsObjectRegistry</c>'s reverse table, which keys on
    /// <see cref="JsValue.ObjectIdentity"/>, sees one object.
    /// </para>
    /// </remarks>
    internal JsValue WrapNode(DomNode node)
    {
        if (_jsObjects.TryGet(node, out var cached))
            return cached;

        // A canonical DomDocument is the document root. The main document is in the node-wrapper map
        // above; a sub-document root's wrapper lives in the document-wrapper map. Resolve it here so
        // e.g. documentElement.parentNode returns the document object, not a fallthrough
        // character-data wrapper.
        if (node is DomDocument documentNode && _jsObjects.TryGetDocument(documentNode, out var documentWrapper))
            return documentWrapper;

        // A <form> gets a wrapper that additionally resolves an unknown name to the control carrying
        // it (HTMLFormElement's named getter). It is decided here rather than in the form binding
        // because a wrapper's type is fixed when it is created, and every member installed below goes
        // on this same object. The lookup itself is FormNamedControls, an IJsExotic the realm consults
        // after ordinary properties — the same handler form.elements uses, so the two cannot answer a
        // name differently.
        var handle = node is DomElement formElement &&
                     string.Equals(formElement.TagName, "form", StringComparison.OrdinalIgnoreCase)
            ? Realm.NewExotic(new Dom.Features.FormNamedControls(formElement, this, missingIsNull: false))
            : Realm.NewObject();

        _jsObjects.Set(node, handle);

        // Point the wrapper at its interface prototype before any member is installed, so
        // constructor.name and Object.getPrototypeOf answer the interface rather than Object.
        // Every node kind InterfaceNameFor names, elements included — ElementInterface.cs says how
        // an element's interface is chosen from its tag.
        ApplyInterfacePrototype(handle, node);

        // Canonical character-data nodes (DomText/DomComment) are not Broiler.Dom.DomElement, so they
        // receive a minimal Node/CharacterData wrapper instead of the full element surface below — the
        // `node is not DomElement` arm after the doctype and fragment arms. Every text and comment node
        // takes it, and the only other kind that reaches it is a DomDocument with no wrapper in either
        // registry map above.
        if (node is DomDocumentType docType)
        {
            // The doctype is a canonical DomDocumentType. It gets the minimal DocumentType surface,
            // not the full element wrapper.
            PopulateDocumentTypeWrapper(handle, docType);
            return handle;
        }

        if (node is DomDocumentFragment fragment)
        {
            // The fragment is a canonical DomDocumentFragment (was a #document-fragment sentinel
            // element). It gets the DocumentFragment container surface
            // (Node base + ParentNode mixin + child manipulation), not the full element wrapper.
            PopulateDocumentFragmentWrapper(handle, fragment);
            return handle;
        }

        if (node is not DomElement element)
        {
            PopulateCharacterDataWrapper(handle, node);
            return handle;
        }


        // Element's whole interface — tagName, id/className, the attribute surface, classList,
        // innerHTML/outerHTML, the shadow-host pair, the ParentNode/ChildNode/element-sibling members,
        // the selector lookups, the box metrics, requestFullscreen and animate — lives on
        // Element.prototype and this wrapper inherits it (DomBridge/ElementInterface.cs). A wrapper
        // minted before the realm carried the interfaces inherits nothing and installs its own, from
        // the same installer, so the two shapes cannot drift.
        if (!_elementInterfacePrototypeReady)
            PopulateElementInterfaceOnInstance(handle, element);

        // HTMLElement's — the global reflectors, style, dataset, innerText/outerText, click/focus/blur,
        // attachInternals, the on* handlers and the offset* metrics — the same way, on
        // HTMLElement.prototype (DomBridge/ElementInterface.cs). An SVG element installs them on
        // itself: SVGElement derives straight from Element, so it inherits none of them, and keeping
        // its own copies is what preserves the surface it has today.
        if (!_htmlElementInterfacePrototypeReady || !IsHtmlNamespace(element))
            PopulateHtmlElementInterfaceOnInstance(handle, element);

        // textContent (read/write) — Node's member, and the element's own: both are the canonical
        // DomNode.TextContent (ElementContentBinding).
        Dom.Features.ElementContentBinding.InstallTextContent(this, handle, element);

        // -- DOM tree navigation --

        // The Node members are on Node.prototype and this wrapper inherits them
        // (DomBridge/NodeInterfaces.cs). A wrapper minted before the realm carried the interfaces
        // inherits nothing and still installs its own.
        if (!_nodeInterfacePrototypesReady)
            PopulateElementNodeMembersOnInstance(handle, element);

        // The CharacterData accessors below are minted by the realm, so each body has a JsCall frame
        // of its own to read its data argument from.

        // data (read/write) — on every element, where it reads undefined and ignores a write
        Realm.DefineAccessor(handle, "data",
            (in call) => Dom.Features.CharacterDataBinding.GetData(element, in call),
            (in call) => Dom.Features.CharacterDataBinding.SetData(this, element, in call));

        // length (read-only) — on every element, where it answers the child count
        Realm.DefineAccessor(handle, "length",
            (in call) => Dom.Features.CharacterDataBinding.GetLength(element, in call),
            null);

        // removeAttributeNodeNS(attr) — the one attribute member that stays the wrapper's own. DOM
        // §4.9 pairs setAttributeNode with setAttributeNodeNS but gives removeAttributeNode no
        // namespace-qualified sibling (an Attr already knows its namespace), so no browser has one and
        // putting it on Element.prototype would give that prototype a member a browser's has not got.
        Realm.DefineMethod(handle, "removeAttributeNodeNS", 1,
            (in call) => _attributes.RemoveAttributeNodeNS(element, handle, in call));

        // The five Node child-mutation members below are minted by the realm, so each body has a
        // JsCall frame of its own to read its child and reference arguments from, and the DOMException
        // a failed step must throw comes from that frame's realm.

        // insertBefore(newChild, refChild)
        Realm.DefineMethod(handle, "insertBefore", 2,
            (in call) => Dom.Features.TreeMutationBinding.InsertBefore(this, element, in call));

        // moveBefore(node, refChild) — the atomic, state-preserving sibling of insertBefore.
        Realm.DefineMethod(handle, "moveBefore", 2,
            (in call) => Dom.Features.TreeMutationBinding.MoveBefore(this, element, in call));

        // -- DOM manipulation methods --

        // HTMLTemplateElement.content — the template contents fragment. Every component idiom goes
        // through it (`importNode(t.content, true)`, `t.content.cloneNode(true)`,
        // `t.content.querySelector(...)`), and without it `t.content` was undefined and the whole
        // component script threw. The fragment is the canonical element's own, created with it and
        // stable for its lifetime, so `t.content === t.content`; a non-null TemplateContents is what
        // makes an element an HTML <template> rather than a tag-name test.
        if (element.TemplateContents is { } templateContents)
        {
            // Nothing but a tree read and a wrapper, so the realm mints the accessor: it names it
            // "get content" and a null setter is how the read-only IDL attribute is spelled.
            Realm.DefineAccessor(handle, "content", (in _) => WrapNode(templateContents), null);
        }

        // appendChild(child)
        Realm.DefineMethod(handle, "appendChild", 1,
            (in call) => Dom.Features.TreeMutationBinding.AppendChild(this, element, in call));

        // removeChild(child)
        Realm.DefineMethod(handle, "removeChild", 1,
            (in call) => Dom.Features.TreeMutationBinding.RemoveChild(this, element, in call));

        // replaceChild(newChild, oldChild)
        Realm.DefineMethod(handle, "replaceChild", 2,
            (in call) => Dom.Features.TreeMutationBinding.ReplaceChild(this, element, in call));

        // -- DOM events --

        // addEventListener / removeEventListener / dispatchEvent are on EventTarget.prototype,
        // routed by receiver (DomBridge/Events.cs) — one function for every target, as
        // in a browser. A wrapper minted before the realm carried it installs its own, through the
        // realm and with a JSEAL frame, exactly as the routed path does.
        if (!_eventTargetRoutingReady)
        {
            Realm.DefineMethod(handle, "addEventListener", 3,
                (in call) => Dom.Features.EventTargetBinding.AddEventListener(this, element, in call));

            Realm.DefineMethod(handle, "removeEventListener", 3,
                (in call) => Dom.Features.EventTargetBinding.RemoveEventListener(this, element, in call));

            Realm.DefineMethod(handle, "dispatchEvent", 1,
                (in call) => Dom.Features.EventTargetBinding.DispatchEvent(this, element, in call));
        }

        // click/focus/blur and the on* handlers are HTMLElement's and are on its prototype
        // (DomBridge/ElementInterface.cs). Compiling the on* HTML attributes into handlers is not
        // a member and still belongs to each element.
        CompileInlineEventAttributes(element);

        // -- Form element support --

        // Form-control IDL reflectors (value/checked/type/name/disabled/required/files) — the
        // co-located FormControlBinding feature module, reached through the
        // IFormControlHost contract (DomBridge/Hosts.Elements.cs). Installed on every element, where
        // a browser gives them only to the interfaces that declare them; the two that are genuinely
        // HTMLElement's, hidden and tabIndex, are on its prototype.
        _formControl.Install(handle, element);

        // checkValidity() — form validation; FormBinding owns the validity check.
        Realm.DefineMethod(handle, "checkValidity", (in _) => JsValue.Boolean(_forms.IsElementValid(element)));

        // reportValidity() — form validation
        Realm.DefineMethod(handle, "reportValidity", (in _) => JsValue.Boolean(_forms.IsElementValid(element)));

        // submit() — for form elements (the co-located FormSubmitBinding feature module, reached
        // through IFormSubmitHost; DomBridge/Hosts.Elements.cs). The method is minted by the realm —
        // which is what gives its body a call frame to build the synthetic event in — and it is handed
        // this wrapper's handle, which becomes the event's target.
        Realm.DefineMethod(handle, "submit",
            (in call) => Dom.Features.FormSubmitBinding.Submit(this, element, handle, in call));

        // getContext(contextType) — for <canvas> elements, in the co-located CanvasBinding feature
        // module. The realm mints the canvas members and everything the 2D context builds, and the
        // seam hands it this wrapper as a handle.
        Dom.Features.CanvasBinding.Install(Realm, this, handle, element);

        // <iframe> browsing-context accessors (contentDocument/contentWindow/getSVGDocument, src/srcdoc
        // read/write, sandbox reflection) — the co-located IframeElementBinding feature module, sibling
        // of the <object> ObjectElementBinding. Reaches the frames machinery through the
        // IIframeElementHost contract (DomBridge/Hosts.Documents.cs).
        Dom.Features.IframeElementBinding.Install(this, handle, element);

        AddElementSpecificMembers(handle, element);

        // Node interface constants (DOM §4.4: these exist on all Node objects) — the type values and
        // the DOCUMENT_POSITION_* bits compareDocumentPosition returns. On Node.prototype, which this
        // wrapper inherits; a wrapper minted before the realm carried it installs its own.
        InstallNodeConstantsIfNotInherited(handle);

        return handle;
    }

    /// <summary>
    /// The <c>Node</c> members as own properties of one element wrapper — the shape every element
    /// had before they moved to <c>Node.prototype</c>, kept for the one case that cannot use them: a
    /// wrapper minted before the realm carried the interfaces, which inherits from nothing.
    /// </summary>
    /// <remarks>
    /// Every member here is the realm's, and the wrapper is only ever named as a handle. The members
    /// and their order are the ones this method installs, which is what
    /// <c>Object.getOwnPropertyNames</c> can see.
    /// </remarks>
    private void PopulateElementNodeMembersOnInstance(JsValue handle, DomElement element)
    {
        // parentNode (read-only, dynamic) — a tree read and a wrapper, so the realm mints it.
        Realm.DefineAccessor(handle, "parentNode",
            (in _) => element.ParentNode != null ? WrapNode(element.ParentNode) : JsValue.Null,
            null);

        Realm.DefineAccessor(handle, "isConnected",
            (in call) => Dom.Features.NodeAccessorsBinding.GetIsConnected(element, in call), null);

        // childNodes (read-only, dynamic)
        Realm.DefineAccessor(handle, "childNodes",
            (in call) => Dom.Features.NodeAccessorsBinding.GetChildNodes(this, element, in call), null);

        // firstChild (read-only, dynamic)
        Realm.DefineAccessor(handle, "firstChild",
            (in call) => Dom.Features.NodeAccessorsBinding.GetFirstChild(this, element, in call), null);

        // lastChild (read-only, dynamic)
        Realm.DefineAccessor(handle, "lastChild",
            (in call) => Dom.Features.NodeAccessorsBinding.GetLastChild(this, element, in call), null);

        // nextSibling (read-only, dynamic)
        Realm.DefineAccessor(handle, "nextSibling",
            (in call) => Dom.Features.NodeAccessorsBinding.GetNextSibling(this, element, in call), null);

        // previousSibling (read-only, dynamic)
        Realm.DefineAccessor(handle, "previousSibling",
            (in call) => Dom.Features.NodeAccessorsBinding.GetPreviousSibling(this, element, in call), null);

        // nodeType (read-only)
        Realm.DefineAccessor(handle, "nodeType",
            (in call) => Dom.Features.NodeAccessorsBinding.GetNodeType(element, in call), null);

        // nodeName (read-only)
        Realm.DefineAccessor(handle, "nodeName",
            (in call) => Dom.Features.NodeAccessorsBinding.GetNodeName(element, in call), null);

        // localName (read-only) — null for non-element nodes; local part of tag name for elements
        Realm.DefineAccessor(handle, "localName",
            (in call) => Dom.Features.NodeAccessorsBinding.GetLocalName(element, in call), null);

        // prefix (read-only) — namespace prefix or null
        Realm.DefineAccessor(handle, "prefix",
            (in call) => Dom.Features.NodeAccessorsBinding.GetPrefix(element, in call), null);

        // namespaceURI (read-only) — returns namespace URI for elements created via createElementNS
        Realm.DefineAccessor(handle, "namespaceURI",
            (in call) => Dom.Features.NodeAccessorsBinding.GetNamespaceURI(element, in call), null);

        // nodeValue (read/write) — null for elements, text content for text/comment nodes
        Realm.DefineAccessor(handle, "nodeValue",
            (in call) => Dom.Features.NodeAccessorsBinding.GetNodeValue(element, in call),
            (in call) => Dom.Features.NodeAccessorsBinding.SetNodeValue(this, element, in call));

        // ownerDocument (read-only) — returns the Document node (nodeType=9)
        Realm.DefineAccessor(handle, "ownerDocument",
            (in call) => Dom.Features.NodeAccessorsBinding.GetOwnerDocument(this, element, in call), null);

        // parentElement (read-only, dynamic) — like parentNode but returns null for non-element parents
        Realm.DefineAccessor(handle, "parentElement",
            (in call) => Dom.Features.NodeAccessorsBinding.GetParentElement(this, element, in call), null);

        // hasChildNodes()
        Realm.DefineMethod(handle, "hasChildNodes", (in _) => JsValue.Boolean(element.ChildNodes.Count > 0));

        // contains(otherNode) — returns true if otherNode is a descendant
        Realm.DefineMethod(handle, "contains", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.Contains(this, element, in call));

        // compareDocumentPosition(otherNode)
        Realm.DefineMethod(handle, "compareDocumentPosition", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.CompareDocumentPosition(this, element, in call));

        // isSameNode(otherNode)
        Realm.DefineMethod(handle, "isSameNode", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.IsSameNode(this, element, in call));

        // normalize()
        Realm.DefineMethod(handle, "normalize", 0,
            (in call) => Dom.Features.NodeRelationshipsBinding.Normalize(this, element, in call));

        // isEqualNode(otherNode)
        Realm.DefineMethod(handle, "isEqualNode", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.IsEqualNode(this, element, in call));

        Realm.DefineMethod(handle, "getRootNode", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.GetRootNode(this, element, in call));

        // cloneNode(deep)
        Realm.DefineMethod(handle, "cloneNode", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.CloneNode(this, element, in call));
    }
}

public sealed partial class DomBridge
{
    /// <summary>
    /// A node's <c>textContent</c>, or <see langword="null"/> for the two node kinds DOM §4.4 gives no
    /// text at all.
    /// </summary>
    /// <remarks>
    /// The text itself is the canonical <see cref="DomNode.TextContent"/>: a character-data node's own
    /// data, and the descendant text of anything else — an element and a fragment alike. Every getter
    /// that wants a JavaScript value
    /// makes one from this; <see cref="JsValue.String(string?)"/> turns the <see langword="null"/> into
    /// JavaScript <c>null</c>, which is exactly the distinction the body is about.
    /// </remarks>
    private static string? NodeTextOrNull(DomNode node) =>
        // DOM §4.4: `textContent` is *null* for a document and for a doctype — they are the two node
        // kinds the algorithm has no text for, rather than kinds whose text happens to be empty. The
        // canonical getter never answers null (a document's is its descendants' text, a doctype's the
        // empty string), so this distinction is the binding's to keep. Answering the empty string for
        // both would make `document.textContent` `""` where Chromium says null, and a page
        // distinguishing the two with `=== null` would read the wrong branch.
        node is DomDocument or DomDocumentType ? null : node.TextContent;

    /// <summary>
    /// Whether the frame <paramref name="element"/> holds is cross-origin to the script asking, which
    /// withholds its <c>contentDocument</c>, its <c>contentWindow</c> and its place in
    /// <c>window.frames</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An <c>http(s)</c> <c>src</c> of another origin than the <em>asking</em> document's answers at
    /// once, without loading anything. The asking document is the one whose script is running -- a
    /// frame's, when a frame's own script reaches its child -- and never simply the top page: judged
    /// against the page, a cross-origin frame's same-origin child was withheld from its own parent.
    /// </para>
    /// <para>
    /// Anything else is not the answer by itself: the frame's document is where its response came
    /// from, and a redirect can take it to another origin, whose <c>document.cookie</c> and whose
    /// identity as a request client the page would otherwise be handed. So the frame is loaded --
    /// through its window, which is how a frame's document is first built everywhere else, so that
    /// what its scripts declare is published on that window -- and its document's own origin judged
    /// (<see cref="IsFrameDocumentCrossOrigin"/>).
    /// </para>
    /// </remarks>
    private bool IsCurrentIframeCrossOrigin(DomElement element)
    {
        if (!HasAttr(element, "srcdoc") &&
            TryGetAttribute(element, "src", out var src) &&
            Internal.Scripting.UrlResolver.Resolve(src, GetInheritedSubDocumentBaseUrl(element)) is { } target &&
            (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps) &&
            CurrentScriptDocumentContext().Origin is { IsOpaque: false } caller &&
            !Broiler.Net.Sites.Origin.FromUrl(target).IsSameOrigin(caller))
        {
            return true;
        }

        try
        {
            _subWindows.GetOrCreate(element);
        }
        catch (Exception ex)
        {
            RenderLogger.LogWarning(LogCategory.JavaScript, "DomBridge.IsCurrentIframeCrossOrigin",
                $"Frame failed to load: {ex.Message}", ex);
            return true;
        }

        return IsFrameDocumentCrossOrigin(element);
    }

    /// <summary>
    /// Whether the document loaded into the frame or object <paramref name="container"/> has a
    /// different origin from the document whose script is running. The caller loads it first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both origins come from the documents' request contexts, which the bridge derives from the URL
    /// each response was actually served from, never from an attribute or from anything page script can
    /// set.
    /// </para>
    /// <para>
    /// <b>An opaque origin is same-origin with nothing but itself.</b> A frame sandboxed without
    /// <c>allow-same-origin</c>, a <c>file:</c> frame, an error document: each has a fresh opaque
    /// origin, and only a script running in that very document shares it. Treating an opaque side as
    /// "not cross-origin" handed the embedder a sandboxed frame's document whatever it had loaded --
    /// including a document a same-origin <c>src</c> had redirected to another site -- so adding
    /// <c>sandbox</c> widened access instead of narrowing it. The one opaque document still judged by
    /// its creator's origin is an unsandboxed <c>data:</c> one (<see cref="AccessOrigin"/>).
    /// </para>
    /// </remarks>
    private bool IsFrameDocumentCrossOrigin(DomElement container) =>
        AreCrossOriginForAccess(FrameDocumentContext(container), CurrentScriptDocumentContext());

    /// <summary>
    /// Whether script of the document <paramref name="second"/> is refused access to the document
    /// <paramref name="first"/>: they are not one document and their access origins differ.
    /// </summary>
    internal bool AreCrossOriginForAccess(Broiler.Net.Http.DocumentRequestContext first, Broiler.Net.Http.DocumentRequestContext second) =>
        !ReferenceEquals(first, second) && !AccessOrigin(first).IsSameOrigin(AccessOrigin(second));

    /// <summary>
    /// The origin a document is judged by when another document's script reaches into it: its own,
    /// except that an unsandboxed <c>data:</c> document is judged by its creator's.
    /// </summary>
    /// <remarks>
    /// A <c>data:</c> frame's origin is opaque, and HTML makes it cross-origin to the page that embeds
    /// it. This bridge has always let a page read the <c>data:</c> frames it builds -- their markup is
    /// the page's own, carried in the URL it wrote -- and much of its behaviour is exercised that way,
    /// so that stays: the frame's cookies, its messages' origin and its requests keep the opaque
    /// origin, only reading its DOM does not. A sandboxed <c>data:</c> frame gets no such allowance.
    /// </remarks>
    private Broiler.Net.Sites.Origin AccessOrigin(Broiler.Net.Http.DocumentRequestContext context) =>
        context.Parent is { } creator &&
        string.Equals(context.DocumentUrl.Scheme, "data", StringComparison.OrdinalIgnoreCase) &&
        !_sandboxedDocumentContexts.Contains(context)
            ? AccessOrigin(creator)
            : context.Origin;

    /// <summary>
    /// Whether the document shown in <paramref name="window"/> -- the top window's or a frame's -- has a
    /// different origin from the document whose script is running: what gates reading a window a
    /// script was handed without asking a container for it (<c>MessageEvent.source</c>, a reference
    /// kept from before its frame navigated).
    /// </summary>
    internal bool IsWindowCrossOriginToCurrentScript(JsValue window)
    {
        var shown = window.IsObject && _browsingContexts.TryGetSubWindowContainer(window, out var container)
            ? FrameDocumentContext(container)
            : TopDocumentContext;
        return AreCrossOriginForAccess(shown, CurrentScriptDocumentContext());
    }
}
