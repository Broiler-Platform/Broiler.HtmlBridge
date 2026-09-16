using System.Text;
using Broiler.CSS;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
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
/// <c>&lt;form&gt;</c> — and all of its members are installed through the realm. This paragraph named
/// an engine-typed sibling, <c>ToJSObject</c>, as one cast over it; that member was retired in bcce315.
/// Wrapper identity lives in <c>Runtime/JsObjectRegistry.cs</c>, whose wrapper-to-node table keys on
/// <see cref="JsValue.ObjectIdentity"/>.
/// </para>
/// <para>
/// <b>Every install site here has migrated, and this paragraph said two had not.</b> A wrapper's
/// members are installed by a dozen modules, and a member cannot be minted by the realm while the body
/// it would call reads the engine's argument frame: there is no adapter between the two call frames,
/// only between the two object types, so each install site migrated when its callee did. The paragraph
/// named <c>EventTargetBinding</c> as the callee that had not; its three members are realm-minted over
/// a <see cref="JsCall"/> at their site below. It named <c>ElementContentBinding.InstallTextContent</c>
/// as still handed the engine object; it is handed the handle. <c>FormControlBinding</c>,
/// <c>IframeElementBinding</c>, the element and HTMLElement interface installers and the per-tag member
/// pass had already moved from an engine object to the handle.
/// </para>
/// <para>
/// <b>There is no engine-typed wrapper factory left.</b> This paragraph kept <c>ToJSObject</c> for the
/// files said to ask for a node's wrapper as the engine's object; bcce315 retired it. Four host
/// contracts name the same operation <c>ToJsObject</c> (<c>IDocumentLevelFactoryHost</c>,
/// <c>IDocumentQueryHost</c>, <c>IDocumentStructureHost</c>, <c>ISubDocumentHost</c>); each forwards here.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    // RF-BRIDGE-1c Phase F (F3b): the JS-object registry is keyed by canonical DomNode, so the
    // DomText/DomComment nodes construction creates (which get JS wrappers) round-trip. (This said
    // they would once construction flipped, and called the widen safe for facade nodes.)
    // P2.2: wrapper identity now lives in JsObjectRegistry, the single authority (was the scattered
    // _jsObjectCache and per-document-root wrapper fields).
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
    /// <see cref="JsValue.ObjectIdentity"/>, sees one object. (This named the retired <c>ToJSObject</c>
    /// as the second route.)
    /// </para>
    /// <para>
    /// <b>This is the implementation, and it used to be the other way round.</b> Building a wrapper
    /// is not one file's work: the members go on it from a dozen modules, and while the ones that had
    /// not migrated still installed engine functions there was nothing to be gained by minting the
    /// object through the realm — the realm would have named it and the engine would still have
    /// furnished it. Every one of those modules reads a <see cref="JsCall"/> now (the class remarks name
    /// the last, <c>EventTargetBinding</c>), so the object is the realm's and nothing here casts it back.
    /// </para>
    /// </remarks>
    internal JsValue WrapNode(DomNode node)
    {
        if (_jsObjects.TryGet(node, out var cached))
            return cached;

        // Phase 4 item 1: a canonical DomDocument is the document root. The main document is in the
        // node-wrapper map above; a sub-document root's wrapper lives in the document-wrapper map
        // (P2.2/P4.4a). Resolve it here so e.g. documentElement.parentNode returns the document
        // object, not a fallthrough character-data wrapper.
        if (node is DomDocument documentNode && _jsObjects.TryGetDocument(documentNode, out var documentWrapper))
            return documentWrapper;

        // A <form> gets a wrapper that additionally resolves an unknown name to the control carrying
        // it (HTMLFormElement's named getter). It is decided here rather than in the form binding
        // because a wrapper's type is fixed when it is created, and every member installed below goes
        // on this same object. The lookup itself is FormNamedControls, an IJsExotic the realm consults
        // after ordinary properties — the same handler form.elements uses, so the two cannot answer a
        // name differently — where it used to be an engine subclass restating that order by hand.
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

        // RF-BRIDGE-1c Phase F (F3c): canonical character-data nodes (DomText/DomComment) are not
        // Broiler.Dom.DomElement, so they receive a minimal Node/CharacterData wrapper instead of the full
        // element surface below — the `node is not DomElement` arm after the doctype and fragment arms. It
        // is live: construction has flipped, so every text and comment node takes it, and the only other
        // kind that reaches it is a DomDocument with no wrapper in either registry map above.
        // (This said the branch was dead on the homogeneous facade tree until that flip.)
        if (node is DomDocumentType docType)
        {
            // Phase 4 item 1: the doctype is a canonical DomDocumentType (was a #doctype sentinel
            // element). It gets the minimal DocumentType surface, not the full element wrapper.
            PopulateDocumentTypeWrapper(handle, docType);
            return handle;
        }

        if (node is DomDocumentFragment fragment)
        {
            // Phase 4 item 1: the fragment is a canonical DomDocumentFragment (was a
            // #document-fragment sentinel element). It gets the DocumentFragment container surface
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

        // textContent (read/write) — Node's member, and deliberately the element's own: its operation
        // differs from the character-data one on Node.prototype (Phase 3 P3.57:
        // ElementContentBinding).
        Dom.Features.ElementContentBinding.InstallTextContent(this, handle, element);

        // -- DOM tree navigation --

        // The Node members are on Node.prototype and this wrapper inherits them
        // (DomBridge/NodeInterfaces.cs). Each was a byte-identical copy of what lives
        // there, so nothing about them changes; only their location does. A wrapper minted before
        // the realm carried the interfaces inherits nothing and still installs its own.
        if (!_nodeInterfacePrototypesReady)
            PopulateElementNodeMembersOnInstance(handle, element);

        // The CharacterData surface below is minted by the realm: its module is migrated, so each body
        // has a JsCall frame of its own to read its offset and data arguments from.

        // data (read/write) — on every element, where it reads undefined and ignores a write
        Realm.DefineAccessor(handle, "data",
            (in call) => Dom.Features.CharacterDataBinding.GetData(element, in call),
            (in call) => Dom.Features.CharacterDataBinding.SetData(this, element, in call));

        // length (read-only) — on every element, where it answers the child count
        Realm.DefineAccessor(handle, "length",
            (in call) => Dom.Features.CharacterDataBinding.GetLength(element, in call),
            null);

        // splitText(offset) — unreachable: no DomElement is a text node, and Text.prototype has it
        if (IsText(element))
        {
            Realm.DefineValue(handle, "splitText",
                Realm.NewMethod("splitText",
                    (in call) => Dom.Features.CharacterDataBinding.SplitText(this, element, in call), 1));
        }

        // substringData and the other four data methods — unreachable too, for the same reason
        if (IsText(element) || IsComment(element))
        {
            Realm.DefineValue(handle, "substringData",
                Realm.NewMethod("substringData",
                    (in call) => Dom.Features.CharacterDataBinding.SubstringData(this, element, in call), 2));

            Realm.DefineValue(handle, "appendData",
                Realm.NewMethod("appendData",
                    (in call) => Dom.Features.CharacterDataBinding.AppendData(this, element, in call), 1));

            Realm.DefineValue(handle, "deleteData",
                Realm.NewMethod("deleteData",
                    (in call) => Dom.Features.CharacterDataBinding.DeleteData(this, element, in call), 2));

            Realm.DefineValue(handle, "insertData",
                Realm.NewMethod("insertData",
                    (in call) => Dom.Features.CharacterDataBinding.InsertData(this, element, in call), 2));

            Realm.DefineValue(handle, "replaceData",
                Realm.NewMethod("replaceData",
                    (in call) => Dom.Features.CharacterDataBinding.ReplaceData(this, element, in call), 3));
        }

        // removeAttributeNodeNS(attr) — the one attribute member that stays the wrapper's own. DOM
        // §4.9 pairs setAttributeNode with setAttributeNodeNS but gives removeAttributeNode no
        // namespace-qualified sibling (an Attr already knows its namespace), so no browser has one and
        // putting it on Element.prototype would give that prototype a member a browser's has not got.
        Realm.DefineValue(handle, "removeAttributeNodeNS",
            Realm.NewMethod("removeAttributeNodeNS",
                (in call) => _attributes.RemoveAttributeNodeNS(element, handle, in call), 1));

        // The five Node child-mutation members below are minted by the realm: TreeMutationBinding is
        // migrated, so each body has a JsCall frame of its own to read its child and reference
        // arguments from, and the DOMException a failed step must throw comes from that frame's realm.

        // insertBefore(newChild, refChild)
        Realm.DefineValue(handle, "insertBefore",
            Realm.NewMethod("insertBefore",
                (in call) => Dom.Features.TreeMutationBinding.InsertBefore(this, element, in call), 2));

        // moveBefore(node, refChild) — the atomic, state-preserving sibling of insertBefore.
        Realm.DefineValue(handle, "moveBefore",
            Realm.NewMethod("moveBefore",
                (in call) => Dom.Features.TreeMutationBinding.MoveBefore(this, element, in call), 2));

        // -- DOM manipulation methods --

        // HTMLTemplateElement.content — the template contents fragment. Every component idiom goes
        // through it (`importNode(t.content, true)`, `t.content.cloneNode(true)`,
        // `t.content.querySelector(...)`), and without it `t.content` was undefined and the whole
        // component script threw. See GetTemplateContent for what this fragment is and is not.
        if (string.Equals(element.TagName, "template", StringComparison.OrdinalIgnoreCase))
        {
            // Nothing but a tree read and a wrapper, so the realm mints the accessor: it names it
            // "get content" and a null setter is how the read-only IDL attribute is spelled.
            Realm.DefineAccessor(handle, "content", (in _) => WrapNode(GetTemplateContent(element)), null);
        }

        // appendChild(child)
        Realm.DefineValue(handle, "appendChild",
            Realm.NewMethod("appendChild",
                (in call) => Dom.Features.TreeMutationBinding.AppendChild(this, element, in call), 1));

        // removeChild(child)
        Realm.DefineValue(handle, "removeChild",
            Realm.NewMethod("removeChild",
                (in call) => Dom.Features.TreeMutationBinding.RemoveChild(this, element, in call), 1));

        // replaceChild(newChild, oldChild)
        Realm.DefineValue(handle, "replaceChild",
            Realm.NewMethod("replaceChild",
                (in call) => Dom.Features.TreeMutationBinding.ReplaceChild(this, element, in call), 2));

        // -- DOM events --

        // addEventListener / removeEventListener / dispatchEvent are on EventTarget.prototype,
        // routed by receiver (DomBridge/Events.cs) — one function for every target, as
        // in a browser. A wrapper minted before the realm carried it installs its own, through the
        // realm and with a JSEAL frame, exactly as the routed path does. (This said the three were the
        // engine's and read the engine's argument frame; neither was so.)
        if (!_eventTargetRoutingReady)
        {
            Realm.DefineValue(handle, "addEventListener",
                Realm.NewMethod("addEventListener",
                    (in call) => Dom.Features.EventTargetBinding.AddEventListener(this, element, in call), 3));

            Realm.DefineValue(handle, "removeEventListener",
                Realm.NewMethod("removeEventListener",
                    (in call) => Dom.Features.EventTargetBinding.RemoveEventListener(this, element, in call), 3));

            Realm.DefineValue(handle, "dispatchEvent",
                Realm.NewMethod("dispatchEvent",
                    (in call) => Dom.Features.EventTargetBinding.DispatchEvent(this, element, in call), 1));
        }

        // click/focus/blur and the on* handlers are HTMLElement's and are on its prototype
        // (DomBridge/ElementInterface.cs). Compiling the on* HTML attributes into handlers is not
        // a member and still belongs to each element.
        CompileInlineEventAttributes(element);

        // -- Form element support --

        // Form-control IDL reflectors (value/checked/type/name/disabled/required/files) — Phase 3
        // P3.60: extracted into the co-located FormControlBinding feature module, reached through the
        // IFormControlHost contract (DomBridge/Hosts.Elements.cs). Installed on every element, where
        // a browser gives them only to the interfaces that declare them; the two that are genuinely
        // HTMLElement's, hidden and tabIndex, are on its prototype.
        _formControl.Install(handle, element);

        // checkValidity() — form validation (Phase 3 P3.9: FormBinding owns the validity check). The
        // body answers a CLR bool and reads no argument, so nothing about it needed an engine frame.
        Realm.DefineValue(handle, "checkValidity",
            Realm.NewMethod("checkValidity", (in _) => JsValue.Boolean(_forms.IsElementValid(element))));

        // reportValidity() — form validation
        Realm.DefineValue(handle, "reportValidity",
            Realm.NewMethod("reportValidity", (in _) => JsValue.Boolean(_forms.IsElementValid(element))));

        // submit() — for form elements (Phase 3 P3.61: co-located FormSubmitBinding feature module,
        // reached through IFormSubmitHost; DomBridge/Hosts.Elements.cs).
        // FormSubmitBinding is migrated: the method is minted by the realm — which is what gives its
        // body a call frame to build the synthetic event in — and it is handed this wrapper's handle,
        // which becomes the event's target. (This said a seam unwrapped it for an engine-typed wrapper.)
        Realm.DefineValue(handle, "submit",
            Realm.NewMethod("submit",
                (in call) => Dom.Features.FormSubmitBinding.Submit(this, element, handle, in call)));

        // getContext(contextType) — for <canvas> elements. Phase 3 P3.64: extracted into the co-located
        // CanvasBinding feature module (unblocked once Phase 6/P8.9 dissolved Broiler.HtmlBridge.Rendering).
        // CanvasBinding is migrated: the realm mints the canvas members and everything the 2D context
        // builds, and the seam hands it this wrapper as a handle.
        Dom.Features.CanvasBinding.Install(Realm, this, handle, element);

        // <iframe> browsing-context accessors (contentDocument/contentWindow/getSVGDocument, src/srcdoc
        // read/write, sandbox reflection) — Phase 3 P3.55: extracted into the co-located IframeElementBinding
        // feature module, sibling of the P3.52 <object> ObjectElementBinding. Reaches the frames machinery
        // through the IIframeElementHost contract (DomBridge/Hosts.Documents.cs).
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
    /// Every member here is the realm's now: <c>NodeAccessorsBinding</c> and
    /// <c>NodeRelationshipsBinding</c> are both migrated, so no body in this method needs an engine
    /// argument frame and the wrapper is only ever named as a handle. The members and their order are
    /// unchanged, which is what <c>Object.getOwnPropertyNames</c> can see.
    /// </remarks>
    private void PopulateElementNodeMembersOnInstance(JsValue handle, DomElement element)
    {
        // parentNode (read-only, dynamic) — a tree read and a wrapper, so the realm mints it.
        Realm.DefineAccessor(handle, "parentNode",
            (in _) => element.ParentNode != null ? WrapNode(element.ParentNode) : JsValue.Null,
            null);

        Realm.DefineAccessor(handle, "isConnected",
            (in call) => Dom.Features.NodeAccessorsBinding.GetIsConnected(this, element, in call), null);

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
        Realm.DefineValue(handle, "hasChildNodes",
            Realm.NewMethod("hasChildNodes", (in _) => JsValue.Boolean(element.ChildNodes.Count > 0)));

        // contains(otherNode) — returns true if otherNode is a descendant
        Realm.DefineValue(handle, "contains",
            Realm.NewMethod("contains",
                (in call) => Dom.Features.NodeRelationshipsBinding.Contains(this, element, in call), 1));

        // compareDocumentPosition(otherNode)
        Realm.DefineValue(handle, "compareDocumentPosition",
            Realm.NewMethod("compareDocumentPosition",
                (in call) => Dom.Features.NodeRelationshipsBinding.CompareDocumentPosition(this, element, in call), 1));

        // isSameNode(otherNode)
        Realm.DefineValue(handle, "isSameNode",
            Realm.NewMethod("isSameNode",
                (in call) => Dom.Features.NodeRelationshipsBinding.IsSameNode(this, element, in call), 1));

        // normalize()
        Realm.DefineValue(handle, "normalize",
            Realm.NewMethod("normalize",
                (in call) => Dom.Features.NodeRelationshipsBinding.Normalize(this, element, in call), 0));

        // isEqualNode(otherNode)
        Realm.DefineValue(handle, "isEqualNode",
            Realm.NewMethod("isEqualNode",
                (in call) => Dom.Features.NodeRelationshipsBinding.IsEqualNode(this, element, in call), 1));

        Realm.DefineValue(handle, "getRootNode",
            Realm.NewMethod("getRootNode",
                (in call) => Dom.Features.NodeRelationshipsBinding.GetRootNode(this, element, in call), 1));

        // cloneNode(deep)
        Realm.DefineValue(handle, "cloneNode",
            Realm.NewMethod("cloneNode",
                (in call) => Dom.Features.NodeRelationshipsBinding.CloneNode(this, element, in call), 1));
    }
}

// The two constant-answer native function factories — UndefinedFunction and NullFunction — are gone
// from here, and so are the TrueFunction and ZeroFunction that stood beside them.
//
// WHY THEY WERE HERE, AND WHY THEY ARE NOT. A DOM member that answers a constant and reads nothing
// still needs a function object, and before the realm could mint one these four built it as the
// engine's own. They were constructable engine functions rather than the bridge's non-constructable
// DOM callable, which is
// observable — navigator.plugins.item.prototype was an object and `new navigator.plugins.item()` did
// not throw — and each module that migrated recorded at its own call site that it was deliberately
// keeping or deliberately correcting that difference: see Features/NavigatorCapabilityBinding.cs,
// Features/ScreenOrientationBinding.cs, Features/SubDocumentBinding.cs, Features/SvgElementBinding.cs
// and Features/TableBinding.cs, each of which says which it chose and why.
//
// The last of those call sites went with the last of those modules, and the factories they left
// uncalled were deleted: TrueFunction and ZeroFunction in b045101, UndefinedFunction and NullFunction
// in 5282d02. No code names one, a page had no name to reach and Object.getOwnPropertyNames no member
// to see, so removing them changed no observable behaviour. The file stays as this note:
// DomBridge/Registration/Registration.cs and Features/IFormSubmitHost.cs cite it, and the five
// above, EventTargetBinding.cs and FormSubmitBinding.cs name its factories, as do comments in
// DomBridge/JsObjects.NonElementNodes.cs and BroilerJsRealm.Members.cs.
// (This said dead private code was still left here, and that five modules pointed at it.)
public sealed partial class DomBridge
{
}

// The five propagation-control callbacks of the synthetic window event — stopPropagation,
// stopImmediatePropagation, preventDefault and the legacy cancelBubble/returnValue setters — are gone
// from here.
//
// WHY THEY WERE HERE, AND WHY THEY ARE NOT. They took an engine argument frame because their only
// caller, DispatchWindowEvent in DomBridge/Lifecycle.cs, installed each of them as an engine function
// over `ref` locals it owned: the installer minted an engine function because the body took the
// engine frame, and the body took the engine frame because the installer minted an engine function.
// That cycle only breaks when both change together, and both are in one file — so when DomBridge/Lifecycle.cs
// migrated, the five became local functions closing on the same four locals the `ref` parameters used
// to carry, in the shape Features/LegacyEventBinding.cs already had for the same five operations on a
// createEvent object. See the remarks on DispatchWindowEvent for that reasoning in full.
//
// Nothing called these afterwards: they were five private methods with a single call site, and the
// call site took its bodies with it. Removing dead private code changes no observable behaviour —
// there is no name for a page to reach and no member for Object.getOwnPropertyNames to see.
public sealed partial class DomBridge
{
}

public sealed partial class DomBridge
{
    /// <summary>
    /// A node's <c>textContent</c>, or <see langword="null"/> for the two node kinds DOM §4.4 gives no
    /// text at all.
    /// </summary>
    /// <remarks>
    /// The algorithm is engine-neutral and always was — it walks the tree and concatenates strings —
    /// so it is stated here in CLR terms, and every getter that wants a JavaScript value makes one
    /// from it — directly, or through <c>IElementContentHost.NodeTextValue</c>.
    /// <see cref="JsValue.String(string?)"/> turns the <see langword="null"/> into
    /// JavaScript <c>null</c>, which is exactly the distinction the next paragraph is about.
    /// </remarks>
    private string? NodeTextOrNull(DomNode node)
    {
        // RF-BRIDGE-1c Phase F (F3c part 2d): character-data nodes expose their data as textContent;
        // an element's textContent is the concatenation of its descendant text.
        if (node is DomCharacterData characterData)
            return characterData.Data;

        // DOM §4.4: `textContent` is *null* for a document and for a doctype — they are the two node
        // kinds the algorithm has no text for, rather than kinds whose text happens to be empty.
        // Both answered the empty string, so `document.textContent` was `""` where Chromium says
        // null, and a page distinguishing the two with `=== null` read the wrong branch.
        if (node is DomDocument or DomDocumentType)
            return null;

        if (node is not DomElement element)
            return string.Empty;

        if (element.ChildNodes.Count > 0)
        {
            var sb = new StringBuilder();
            CollectTextContent(element, sb);
            return sb.ToString();
        }

        // A childless element has empty textContent (its content, if any, is canonical DomText
        // children handled above — Phase 4 item 3 removed the parallel InnerHtml fallback).
        return string.Empty;
    }

    private bool IsCurrentIframeCrossOrigin(DomElement element)
    {
        if (HasAttr(element, "srcdoc"))
            return false;

        var iframeSrcValue = TryGetAttribute(element, "src", out var srcVal) ? srcVal : string.Empty;
        return IsCrossOrigin(iframeSrcValue, _pageUrl);
    }

    // MutationObserver option parsing and observe()/disconnect() registration moved to the Phase 3
    // MutationObserverBinding feature module (Broiler.HtmlBridge.Dom.Features).

    private bool IsPositionAfter(DomNode docRoot, DomNode containerA, int offsetA, DomNode containerB, int offsetB)
    {
        // Phase 4 item 4/5: for boundary points in the SAME tree this is exactly canonical
        // Broiler.Dom.DomRange.CompareBoundaryPoints(...) > 0 (verified branch-for-branch: same-container,
        // either-descendant, and common-ancestor ordering all agree), so delegate to it instead of
        // re-implementing the walk. The bridge deliberately keeps a LENIENT cross-tree path — canonical
        // throws WrongDocument, but the bridge's compareBoundaryPoints returns an order rather than
        // throwing — so the cross-tree branch (different roots) is preserved verbatim below.
        if (ReferenceEquals(containerA.GetRootNode(), containerB.GetRootNode()))
            return DomRange.CompareBoundaryPoints(containerA, offsetA, containerB, offsetB) > 0;

        var allNodes = docRoot.InclusiveDescendants().ToList();
        var idxA = allNodes.IndexOf(containerA);
        var idxB = allNodes.IndexOf(containerB);
        if (idxA < 0 || idxB < 0)
            return false;

        return idxA > idxB || (idxA == idxB && offsetA > offsetB);
    }


    private int CompareBoundaryPosition(DomNode docRoot, DomNode containerA, int offsetA, DomNode containerB, int offsetB)
    {
        if (ReferenceEquals(containerA, containerB) && offsetA == offsetB)
            return 0;

        if (IsPositionAfter(docRoot, containerA, offsetA, containerB, offsetB))
            return 1;

        if (IsPositionAfter(docRoot, containerB, offsetB, containerA, offsetA))
            return -1;

        return 0;
    }
}

public sealed partial class DomBridge
{

    // HTMLElement global content-attribute reflectors (id, className, title, lang, accessKey, dir,
    // draggable) moved to the GlobalAttributeBinding feature module (Phase 3 P3.54).

    // innerHTML / outerHTML / textContent get+set moved to the ElementContentBinding feature module
    // (Phase 3 P3.57).

    // element.shadowRoot getter moved to the ShadowDomBinding feature module (Phase 3 P3.62).

    // element.style = "..." cssText assignment setter moved to StyleDeclarationBinding (Phase 3 P3.63).

    // insertBefore(newChild, refChild) moved to the TreeMutationBinding feature module (Phase 3 P3.58).

    // attachShadow(init) moved to the ShadowDomBinding feature module (Phase 3 P3.62).

    // appendChild / append / prepend / removeChild / replaceChild moved to the TreeMutationBinding
    // feature module (Phase 3 P3.58).

    // get/set on<event> inline event-handler reflectors moved to the EventHandlerReflectorBinding
    // feature module (Phase 3 P3.59).

    // Form-control IDL reflectors (value/checked/type/name/disabled/hidden/tabIndex/required) moved to
    // the FormControlBinding feature module (Phase 3 P3.60).

    // form.submit() moved to the FormSubmitBinding feature module (Phase 3 P3.61).

    // insertAdjacentElement / insertAdjacentText / insertAdjacentHTML (and their
    // NormalizeInsertAdjacentPosition / GetInsertAdjacentTarget helpers) moved to the
    // InsertAdjacentBinding feature module (Phase 3 P3.56).

    // canvas.getContext("2d") (and its BuildCanvas2DContext + JsUtilities…034…058Core drawing callbacks)
    // moved to the CanvasBinding feature module (Phase 3 P3.64) — the last element-member callback in the
    // mixed JsObjects.cs file, unblocked once Phase 6/P8.9 dissolved Broiler.HtmlBridge.Rendering into Dom.

    // <iframe> browsing-context accessors (contentDocument/contentWindow/getSVGDocument, src/srcdoc
    // setters) moved to the IframeElementBinding feature module (Phase 3 P3.55).

}

public sealed partial class DomBridge
{

    // form.elements.length moved to the Phase 3 FormBinding feature module
    // (Broiler.HtmlBridge.Dom.Features).


    // classList operations delegate to the canonical Broiler.Dom.DomTokenList
    // ordered-set algorithm (parse/serialize on ASCII whitespace, unique-ordered,
    // attribute-synchronized). The bridge keeps only the JavaScript argument
    // marshaling, the lenient empty-token skip these methods have always applied,
    // and the style-scope invalidation callback.
    // classList / DOMTokenList callbacks (contains/add/remove/toggle/replace) moved to the Phase 3
    // ClassListBinding feature module (Broiler.HtmlBridge.Dom.Features).

    // Canvas 2D context callbacks (setFillStyle/…/measureText, formerly JsUtilities…034…058Core) moved to
    // the Phase 3 (P3.64) CanvasBinding feature module (Broiler.HtmlBridge.Dom.Features), unblocked once
    // Phase 6/P8.9 dissolved Broiler.HtmlBridge.Rendering into Dom.

}
