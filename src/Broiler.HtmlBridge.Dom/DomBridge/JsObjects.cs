using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

/// <summary>
/// The node-wrapper hub: it turns a <see cref="DomNode"/> into the JavaScript object a page holds for
/// it, dispatching by node kind and then installing — or, where an interface prototype carries them,
/// deliberately not installing — that kind's whole member surface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two names for one wrapper, and the file says which is which.</b> <see cref="WrapNode"/> is the
/// JSEAL-vocabulary entry point and <see cref="ToJSObject"/> the engine-typed one; they answer the
/// same instance, because a JSEAL object handle carries the engine's own object. Wrapper identity
/// lives in <c>Runtime/JsObjectRegistry.cs</c> and is untouched by either.
/// </para>
/// <para>
/// <b>What is still engine-typed here is pinned from outside, not left behind.</b> A wrapper's
/// members are installed by a dozen modules, and a member cannot be minted by the realm while the body
/// it would call takes an <c>Arguments</c>: there is no adapter between the two call frames, only
/// between the two object types. So each install site migrates when its callee does. Through the realm
/// already: <c>CharacterDataBinding</c>, <c>NodeAccessorsBinding</c>, <c>NodeRelationshipsBinding</c>,
/// <c>TreeMutationBinding</c>, <c>AttributesBinding</c>, <c>FormSubmitBinding</c>,
/// <c>CanvasBinding</c>, and the handful whose bodies read nothing but the DOM. Still on the engine's
/// frame, and each named at its site below: <c>EventTargetBinding</c>, <c>FormControlBinding</c>,
/// <c>IframeElementBinding</c>, <c>ElementContentBinding</c>, the element and HTMLElement interface
/// installers, and the per-tag member pass.
/// </para>
/// <para>
/// <b>The wrapper object itself is the last thing to flip, and <see cref="ToJSObject"/> is why.</b>
/// Thirty files across this assembly ask for a node's wrapper as the engine's object, so the object
/// this method mints, the prototype it is linked through, and every unmigrated installer handed
/// <c>obj</c> are all typed by that one return type. It stays until those callers ask
/// <see cref="WrapNode"/> instead.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    private const double DefaultBodyMarginPixels = 8;
    private const int MaxScrollContinuationDepth = 16;

    // RF-BRIDGE-1c Phase F (F3b): the JS-object registry is keyed by canonical DomNode so
    // text/comment nodes (which get JS wrappers) can round-trip once they flip to canonical
    // DomText/DomComment. A facade node IS-A DomNode, so this is a behaviour-preserving widen.
    // P2.2: wrapper identity now lives in JsObjectRegistry, the single authority (was the scattered
    // _jsObjectCache/_docRootToDocJSObject fields).
    private readonly Dom.Runtime.JsObjectRegistry _jsObjects = new();
    /// <summary>Counter for tracking top-layer insertion order via showModal().</summary>
    private int _topLayerCounter;

    /// <summary>
    /// A node's JS wrapper as a JSEAL handle — the engine-neutral name for what
    /// <see cref="ToJSObject"/> answers, and the one a migrated binding asks for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is the same object, not a conversion.</b> A JSEAL object handle carries the engine's own
    /// <c>JSObject</c>, so a wrapper reached through here and one reached through
    /// <see cref="ToJSObject"/> are the same instance: <c>el === el</c> holds, and the seven
    /// <c>ConditionalWeakTable</c>s the bridge keys on wrapper identity — <c>JsObjectRegistry</c>
    /// first among them — keep answering the question they always asked.
    /// </para>
    /// <para>
    /// <b>Why this delegates to <see cref="ToJSObject"/> rather than the other way round.</b> Building
    /// a wrapper is not one file's work: the members go on it from a dozen modules — the attribute
    /// surface, the tree mutations, the event target, the form controls, the element and HTMLElement
    /// interface installers — and the ones that have not migrated still take an engine argument frame
    /// and install an engine function. A wrapper minted by
    /// <see cref="IJsRealm.NewObject"/> would be handed straight back to them, so the realm would name
    /// the object and the engine would still furnish it. The direction inverts, and this method
    /// becomes the implementation, when those modules land; until then the honest shape is a handle
    /// over what they build.
    /// </para>
    /// </remarks>
    internal JsValue WrapNode(DomNode node) =>
        Dom.Runtime.JsInterop.FromEngineObject(ToJSObject(node));

    /// <summary>
    /// A node's JS wrapper, as the engine object the unmigrated half of the bridge holds.
    /// </summary>
    /// <remarks>
    /// <b>This is the engine-typed adapter and it stays one deliberately.</b> It is the most-called
    /// method in the bridge — thirty files across this assembly reach for it, twenty-nine of them
    /// outside this file — so migrating its <em>return type</em> would ripple into every one of them at
    /// once, which is the change this file-by-file port exists to avoid. <see cref="WrapNode"/> is the
    /// JSEAL-vocabulary sibling; a migrated caller asks for that and everything else keeps asking for
    /// this.
    /// </remarks>
    internal JSObject ToJSObject(DomNode node)
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
        // on this same object.
        var obj = node is DomElement formElement &&
                  string.Equals(formElement.TagName, "form", StringComparison.OrdinalIgnoreCase)
            ? new Dom.Features.FormElementJSObject(formElement, this)
            : new JSObject();
        _jsObjects.Set(node, obj);

        // The same wrapper, named the way a migrated installer asks for it. Every member below that
        // the realm mints goes on this handle, and every member the engine still mints goes on `obj`;
        // they are one object, so the two halves cannot drift apart.
        var handle = Dom.Runtime.JsInterop.FromEngineObject(obj);

        // Point the wrapper at its interface prototype before any member is installed, so
        // constructor.name and Object.getPrototypeOf answer the interface rather than Object.
        // Non-element nodes only — see WrapperPrototypes.cs for why an element's is a separate
        // question.
        ApplyInterfacePrototype(obj, node);

        // RF-BRIDGE-1c Phase F (F3c): canonical character-data nodes (DomText/DomComment) are not
        // Broiler.Dom.DomElement, so they receive a minimal Node/CharacterData wrapper instead of the full
        // element surface below. This branch is dead on today's homogeneous facade tree — facade
        // text/comment nodes are Broiler.Dom.DomElement and fall through to the element wrapper, preserving
        // behaviour — and goes live once text/comment construction flips to canonical
        // DomText/DomComment (F3c construction cutover).
        if (node is DomDocumentType docType)
        {
            // Phase 4 item 1: the doctype is a canonical DomDocumentType (was a #doctype sentinel
            // element). It gets the minimal DocumentType surface, not the full element wrapper.
            PopulateDocumentTypeWrapper(handle, docType);
            return obj;
        }

        if (node is DomDocumentFragment fragment)
        {
            // Phase 4 item 1: the fragment is a canonical DomDocumentFragment (was a
            // #document-fragment sentinel element). It gets the DocumentFragment container surface
            // (Node base + ParentNode mixin + child manipulation), not the full element wrapper.
            PopulateDocumentFragmentWrapper(handle, fragment);
            return obj;
        }

        if (node is not DomElement element)
        {
            PopulateCharacterDataWrapper(handle, node);
            return obj;
        }


        // Element's whole interface — tagName, id/className, the attribute surface, classList,
        // innerHTML/outerHTML, the shadow-host pair, the ParentNode/ChildNode/element-sibling members,
        // the selector lookups, the box metrics, requestFullscreen and animate — lives on
        // Element.prototype and this wrapper inherits it (DomBridge.ElementInterface.cs). A wrapper
        // minted before the realm carried the interfaces inherits nothing and installs its own, from
        // the same installer, so the two shapes cannot drift.
        if (!_elementInterfacePrototypeReady)
            PopulateElementInterfaceOnInstance(obj, element);

        // HTMLElement's — the global reflectors, style, dataset, innerText/outerText, click/focus/blur,
        // attachInternals, the on* handlers and the offset* metrics — the same way, on
        // HTMLElement.prototype (DomBridge.HtmlElementInterface.cs). An SVG element installs them on
        // itself: SVGElement derives straight from Element, so it inherits none of them, and keeping
        // its own copies is what preserves the surface it has today.
        if (!_htmlElementInterfacePrototypeReady || !IsHtmlNamespace(element))
            PopulateHtmlElementInterfaceOnInstance(obj, element);

        // textContent (read/write) — Node's member, and deliberately the element's own: its operation
        // differs from the character-data one on Node.prototype (Phase 3 P3.57:
        // ElementContentBinding).
        Dom.Features.ElementContentBinding.InstallTextContent(this, obj, element);

        // -- DOM tree navigation --

        // The Node members are on Node.prototype and this wrapper inherits them
        // (DomBridge.CharacterDataInterface.cs). Each was a byte-identical copy of what lives
        // there, so nothing about them changes; only their location does. A wrapper minted before
        // the realm carried the interfaces inherits nothing and still installs its own.
        if (!_nodeInterfacePrototypesReady)
            PopulateElementNodeMembersOnInstance(handle, element);

        // The CharacterData surface below is minted by the realm: its module is migrated, so each body
        // has a JsCall frame of its own to read its offset and data arguments from.

        // data (read/write) — for text nodes and comment nodes (alias for nodeValue/textContent)
        Realm.DefineAccessor(handle, "data",
            (in call) => Dom.Features.CharacterDataBinding.GetData(element, in call),
            (in call) => Dom.Features.CharacterDataBinding.SetData(this, element, in call));

        // length (read-only) — character count for text/comment nodes, child count for elements
        Realm.DefineAccessor(handle, "length",
            (in call) => Dom.Features.CharacterDataBinding.GetLength(element, in call),
            null);

        // splitText(offset) — splits a text node at the given character offset
        if (IsText(element))
        {
            Realm.DefineValue(handle, "splitText",
                Realm.NewMethod("splitText",
                    (in call) => Dom.Features.CharacterDataBinding.SplitText(this, element, in call), 1));
        }

        // substringData(offset, count) — for text/comment CharacterData nodes
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
        // routed by receiver (DomBridge.EventTargetInterface.cs) — one function for every target, as
        // in a browser. A wrapper minted before the realm carried it installs its own, and those three
        // are the engine's: EventTargetBinding takes an Arguments, so they move when it does.
        if (!_eventTargetRoutingReady)
        {
            obj.FastAddValue("addEventListener",
                new DomFunction((in a) => Dom.Features.EventTargetBinding.AddEventListener(this, element, in a), "addEventListener", 3),
                JSPropertyAttributes.EnumerableConfigurableValue);

            obj.FastAddValue("removeEventListener",
                new DomFunction((in a) => Dom.Features.EventTargetBinding.RemoveEventListener(this, element, in a), "removeEventListener", 3),
                JSPropertyAttributes.EnumerableConfigurableValue);

            obj.FastAddValue("dispatchEvent",
                new DomFunction((in a) => Dom.Features.EventTargetBinding.DispatchEvent(this, element, in a), "dispatchEvent", 1),
                JSPropertyAttributes.EnumerableConfigurableValue);
        }

        // click/focus/blur and the on* handlers are HTMLElement's and are on its prototype
        // (DomBridge.HtmlElementInterface.cs). Compiling the on* HTML attributes into handlers is not
        // a member and still belongs to each element.
        CompileInlineEventAttributes(element);

        // -- Form element support --

        // Form-control IDL reflectors (value/checked/type/name/disabled/required/files) — Phase 3
        // P3.60: extracted into the co-located FormControlBinding feature module, reached through the
        // IFormControlHost contract (DomBridge.FormControlHost.cs). Installed on every element, where
        // a browser gives them only to the interfaces that declare them; the two that are genuinely
        // HTMLElement's, hidden and tabIndex, are on its prototype.
        _formControl.Install(obj, element);

        // checkValidity() — form validation (Phase 3 P3.9: FormBinding owns the validity check). The
        // body answers a CLR bool and reads no argument, so nothing about it needed an engine frame.
        Realm.DefineValue(handle, "checkValidity",
            Realm.NewMethod("checkValidity", (in _) => JsValue.Boolean(_forms.IsElementValid(element))));

        // reportValidity() — form validation
        Realm.DefineValue(handle, "reportValidity",
            Realm.NewMethod("reportValidity", (in _) => JsValue.Boolean(_forms.IsElementValid(element))));

        // submit() — for form elements (Phase 3 P3.61: co-located FormSubmitBinding feature module,
        // reached through IFormSubmitHost; DomBridge.FormSubmitHost.cs).
        // FormSubmitBinding is migrated: the method is minted by the realm — which is what gives its
        // body a call frame to build the synthetic event in — and the seam unwraps the handle for
        // this engine-typed wrapper, and wraps the wrapper as the event's target.
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
        // through the IIframeElementHost contract (DomBridge.IframeElementHost.cs).
        Dom.Features.IframeElementBinding.Install(this, obj, element);

        AddElementSpecificMembers(obj, element);

        // Node interface constants (DOM §4.4: these exist on all Node objects) — the type values and
        // the DOCUMENT_POSITION_* bits compareDocumentPosition returns. On Node.prototype, which this
        // wrapper inherits; a wrapper minted before the realm carried it installs its own.
        InstallNodeConstantsIfNotInherited(handle);

        return obj;
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
