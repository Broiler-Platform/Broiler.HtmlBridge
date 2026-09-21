using Broiler.Dom;
using Broiler.JSeal;
using static Broiler.HtmlBridge.DomBridgeUtils;
using static Broiler.HtmlBridge.DomBridgeHostUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// Sibling partial peeled out of <c>JsObjects.cs</c> to keep it under
/// the 750-line guideline: the non-element node JS-wrapper populators. Builds the minimal JS surface for
/// canonical character-data nodes (<c>DomText</c>/<c>DomComment</c>), <c>DocumentType</c>, and
/// <c>DocumentFragment</c> — the counterparts to the element wrapper that <see cref="WrapNode"/>
/// (still in <c>JsObjects.cs</c>) dispatches to for these node kinds. Pure partial-class
/// relocation — no signature, accessibility, or logic change.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each populator takes the wrapper as a handle and never reaches for the engine object: no
/// member's body needs one.</b> Every member whose body reads nothing but the DOM tree — the
/// tree links, the element views, <c>textContent</c>, the fragment's own child manipulation — is
/// minted by the realm, and so now is everything <c>NodeAccessorsBinding</c>,
/// <c>CharacterDataBinding</c> and <c>NodeRelationshipsBinding</c> answer: those three modules are
/// migrated, so their bodies have a <see cref="JsCall"/> frame of their own.
/// </para>
/// <para>
/// Nothing is left engine-typed. This remark said one thing was: that <c>EventTargetBinding</c> took
/// an engine argument frame, so the <c>addEventListener</c>/<c>removeEventListener</c>/
/// <c>dispatchEvent</c> members were minted by the engine and each populator unwrapped the handle for
/// them alone. Each populator mints the three through the realm over a <see cref="JsCall"/>, the same
/// bodies <c>EventTarget.prototype</c>'s routed methods call (<c>DomBridge/Events.cs</c>),
/// and unwraps nothing.
/// </para>
/// <para>
/// <c>FindInDescendants</c> answers a handle, so the fragment's two selector members are the realm's
/// over it.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>
    /// The JS wrapper for a canonical <c>DomText</c>/<c>DomComment</c> — the members a character-data
    /// node exposes (no tagName/style/attributes/querySelector/form/iframe surface).
    /// Populated onto the already-cached <paramref name="handle"/> (the caller registers it in the
    /// <c>JsObjectRegistry</c> before calling, so re-entrant <see cref="WrapNode"/> lookups resolve).
    /// With the interfaces registered it installs nothing: the Node, CharacterData, Text and ChildNode
    /// members are inherited, and <c>EventTarget.prototype</c> routes the three listener methods. Only a
    /// wrapper minted before that installs them, and the Node constants, as its own.
    /// </summary>
    private void PopulateCharacterDataWrapper(JsValue handle, DomNode node)
    {
        // The Node, CharacterData and Text members live on the interface prototypes
        // (DomBridge/NodeInterfaces.cs), which this wrapper inherits — so there is nothing
        // to install here and Object.getOwnPropertyNames(textNode) is the [] a browser gives.
        //
        // Unless the realm was not up when this wrapper was minted, in which case
        // ApplyInterfacePrototype linked nothing and there is no prototype to inherit from. Then the
        // members go on the instance exactly as they always did, which is the old shape rather than
        // a broken one.
        if (!_nodeInterfacePrototypesReady)
            PopulateCharacterDataMembersOnInstance(handle, node);

        // addEventListener / removeEventListener / dispatchEvent are on EventTarget.prototype,
        // routed by receiver (DomBridge/Events.cs) — one function for every target, as
        // in a browser. A wrapper minted before the realm carried it installs its own.
        if (!_eventTargetRoutingReady)
        {

            Realm.DefineMethod(handle, "addEventListener", 3,
                (in call) => Dom.Features.EventTargetBinding.AddEventListener(this, node, in call));

            Realm.DefineMethod(handle, "removeEventListener", 3,
                (in call) => Dom.Features.EventTargetBinding.RemoveEventListener(this, node, in call));

            Realm.DefineMethod(handle, "dispatchEvent", 1,
                (in call) => Dom.Features.EventTargetBinding.DispatchEvent(this, node, in call));
        }

    }

    /// <summary>
    /// The <c>Node</c>, <c>CharacterData</c> and <c>Text</c> members as own properties of one
    /// wrapper — the shape every character-data node had before those members moved to the interface
    /// prototypes, kept for the one case that cannot use them: a wrapper minted before the realm
    /// carried the interfaces, which inherits from nothing.
    /// </summary>
    private void PopulateCharacterDataMembersOnInstance(JsValue handle, DomNode node)
    {
        // -- Node identity --
        Realm.DefineAccessor(handle, "nodeType",
            (in call) => Dom.Features.NodeAccessorsBinding.GetNodeType(node, in call), null);

        Realm.DefineAccessor(handle, "nodeName",
            (in call) => Dom.Features.NodeAccessorsBinding.GetNodeName(node, in call), null);

        Realm.DefineAccessor(handle, "localName",
            (in call) => Dom.Features.NodeAccessorsBinding.GetLocalName(node, in call), null);

        Realm.DefineAccessor(handle, "prefix",
            (in call) => Dom.Features.NodeAccessorsBinding.GetPrefix(node, in call), null);

        Realm.DefineAccessor(handle, "namespaceURI",
            (in call) => Dom.Features.NodeAccessorsBinding.GetNamespaceURI(node, in call), null);

        // -- Character data --
        Realm.DefineAccessor(handle, "nodeValue",
            (in call) => Dom.Features.NodeAccessorsBinding.GetNodeValue(node, in call),
            (in call) => Dom.Features.NodeAccessorsBinding.SetNodeValue(this, node, in call));

        // textContent's setter is NodeAccessorsBinding's, which is migrated, so the pair is the
        // realm's. JsValue.String turns the "no text at all" null into JavaScript null, which is the
        // same value the engine-typed GetNodeTextValue adapter produced for this getter. The setter is
        // the canonical one every node kind uses rather than nodeValue's, because textContent is a
        // nullable DOMString: `text.textContent = null` empties the data instead of writing "null".
        Realm.DefineAccessor(handle, "textContent",
            (in _) => JsValue.String(NodeTextOrNull(node)),
            (in call) => Dom.Features.NodeAccessorsBinding.SetTextContent(node, in call));

        Realm.DefineAccessor(handle, "data",
            (in call) => Dom.Features.CharacterDataBinding.GetData(node, in call),
            (in call) => Dom.Features.CharacterDataBinding.SetData(this, node, in call));

        Realm.DefineAccessor(handle, "length",
            (in call) => Dom.Features.CharacterDataBinding.GetLength(node, in call), null);

        // splitText is Text-only (not on Comment).
        if (IsText(node))
        {
            Realm.DefineMethod(handle, "splitText", 1,
                (in call) => Dom.Features.CharacterDataBinding.SplitText(this, node, in call));
        }

        Realm.DefineMethod(handle, "substringData", 2,
            (in call) => Dom.Features.CharacterDataBinding.SubstringData(this, node, in call));

        Realm.DefineMethod(handle, "appendData", 1,
            (in call) => Dom.Features.CharacterDataBinding.AppendData(this, node, in call));

        Realm.DefineMethod(handle, "deleteData", 2,
            (in call) => Dom.Features.CharacterDataBinding.DeleteData(this, node, in call));

        Realm.DefineMethod(handle, "insertData", 2,
            (in call) => Dom.Features.CharacterDataBinding.InsertData(this, node, in call));

        Realm.DefineMethod(handle, "replaceData", 3,
            (in call) => Dom.Features.CharacterDataBinding.ReplaceData(this, node, in call));

        // -- Tree navigation --
        Realm.DefineAccessor(handle, "parentNode",
            (in _) => node.ParentNode != null ? WrapNode(node.ParentNode) : JsValue.Null,
            null);

        Realm.DefineAccessor(handle, "parentElement",
            (in call) => Dom.Features.NodeAccessorsBinding.GetParentElement(this, node, in call), null);

        Realm.DefineAccessor(handle, "isConnected",
            (in call) => Dom.Features.NodeAccessorsBinding.GetIsConnected(node, in call), null);

        Realm.DefineAccessor(handle, "childNodes",
            (in call) => Dom.Features.NodeAccessorsBinding.GetChildNodes(this, node, in call), null);

        Realm.DefineAccessor(handle, "firstChild",
            (in call) => Dom.Features.NodeAccessorsBinding.GetFirstChild(this, node, in call), null);

        Realm.DefineAccessor(handle, "lastChild",
            (in call) => Dom.Features.NodeAccessorsBinding.GetLastChild(this, node, in call), null);

        Realm.DefineAccessor(handle, "nextSibling",
            (in call) => Dom.Features.NodeAccessorsBinding.GetNextSibling(this, node, in call), null);

        Realm.DefineAccessor(handle, "previousSibling",
            (in call) => Dom.Features.NodeAccessorsBinding.GetPreviousSibling(this, node, in call), null);

        Realm.DefineAccessor(handle, "ownerDocument",
            (in call) => Dom.Features.NodeAccessorsBinding.GetOwnerDocument(this, node, in call), null);

        Realm.DefineMethod(handle, "hasChildNodes", (in _) => JsValue.Boolean(node.ChildNodes.Count > 0));

        // -- Node methods --
        Realm.DefineMethod(handle, "cloneNode", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.CloneNode(this, node, in call));

        Realm.DefineMethod(handle, "contains", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.Contains(this, node, in call));

        Realm.DefineMethod(handle, "compareDocumentPosition", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.CompareDocumentPosition(this, node, in call));

        Realm.DefineMethod(handle, "isSameNode", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.IsSameNode(this, node, in call));

        Realm.DefineMethod(handle, "isEqualNode", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.IsEqualNode(this, node, in call));

        Realm.DefineMethod(handle, "getRootNode", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.GetRootNode(this, node, in call));

        Realm.DefineMethod(handle, "normalize", 0,
            (in call) => Dom.Features.NodeRelationshipsBinding.Normalize(this, node, in call));

        // -- ChildNode mixin --
        // The realm's: ChildNodeBinding reads a JsCall frame at one entry point per operation, which
        // this file, NodeInterfaces.cs and ElementInterface.cs all share.
        Realm.DefineMethod(handle, "remove",
            (in call) => Dom.Features.ChildNodeBinding.Remove(this, node, in call));

        Realm.DefineMethod(handle, "before",
            (in call) => Dom.Features.ChildNodeBinding.Before(this, node, in call));

        Realm.DefineMethod(handle, "after",
            (in call) => Dom.Features.ChildNodeBinding.After(this, node, in call));

        Realm.DefineMethod(handle, "replaceWith",
            (in call) => Dom.Features.ChildNodeBinding.ReplaceWith(this, node, in call));


        // The constants are on Node.prototype for every other wrapper; this one inherits nothing.
        Dom.Features.NodeConstantsBinding.Install(Realm, handle);
    }

    /// <summary>
    /// Builds the JS wrapper for a canonical <see cref="DomDocumentType"/> node.
    /// A DocumentType is a leaf Node with the ChildNode mixin and DocumentType-specific
    /// <c>name</c>/<c>publicId</c>/<c>systemId</c>/<c>internalSubset</c> — it deliberately does NOT get
    /// the element surface (attributes/style/children) it inherited while it was a <c>#doctype</c>
    /// sentinel element, nor the CharacterData mutation methods. The node-generic handlers are the
    /// same ones the character-data wrapper uses.
    /// </summary>
    private void PopulateDocumentTypeWrapper(JsValue handle, DomDocumentType doctype)
    {
        DomNode node = doctype;

        // -- Node identity --
        Realm.DefineAccessor(handle, "nodeType",
            (in call) => Dom.Features.NodeAccessorsBinding.GetNodeType(node, in call), null);

        Realm.DefineAccessor(handle, "nodeName",
            (in call) => Dom.Features.NodeAccessorsBinding.GetNodeName(node, in call), null);

        // A doctype has no value and no text: DOM §4.4 gives it null for both, and the realm mints
        // the two read-only accessors.
        Realm.DefineAccessor(handle, "nodeValue", (in _) => JsValue.Null, null);

        Realm.DefineAccessor(handle, "textContent", (in _) => JsValue.Null, null);

        // -- DocumentType interface --
        Realm.DefineAccessor(handle, "name", (in _) => JsValue.String(doctype.Name), null);

        Realm.DefineAccessor(handle, "publicId",
            (in call) => Dom.Features.NodeAccessorsBinding.GetPublicId(node, in call), null);

        Realm.DefineAccessor(handle, "systemId",
            (in call) => Dom.Features.NodeAccessorsBinding.GetSystemId(node, in call), null);

        // internalSubset is always null — this parser keeps no subset. The realm's accessor is
        // non-constructable, which is what WebIDL says an attribute getter is.
        Realm.DefineAccessor(handle, "internalSubset", (in _) => JsValue.Null, null);

        // -- Tree navigation --
        Realm.DefineAccessor(handle, "parentNode",
            (in _) => node.ParentNode != null ? WrapNode(node.ParentNode) : JsValue.Null,
            null);

        Realm.DefineAccessor(handle, "parentElement",
            (in call) => Dom.Features.NodeAccessorsBinding.GetParentElement(this, node, in call), null);

        Realm.DefineAccessor(handle, "previousSibling",
            (in call) => Dom.Features.NodeAccessorsBinding.GetPreviousSibling(this, node, in call), null);

        Realm.DefineAccessor(handle, "nextSibling",
            (in call) => Dom.Features.NodeAccessorsBinding.GetNextSibling(this, node, in call), null);

        Realm.DefineAccessor(handle, "ownerDocument",
            (in call) => Dom.Features.NodeAccessorsBinding.GetOwnerDocument(this, node, in call), null);

        Realm.DefineAccessor(handle, "isConnected",
            (in call) => Dom.Features.NodeAccessorsBinding.GetIsConnected(node, in call), null);

        // A doctype is a leaf: hasChildNodes is constantly false.
        Realm.DefineMethod(handle, "hasChildNodes", (in _) => JsValue.False);

        // -- Node methods --
        Realm.DefineMethod(handle, "cloneNode", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.CloneNode(this, node, in call));

        Realm.DefineMethod(handle, "isEqualNode", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.IsEqualNode(this, node, in call));

        Realm.DefineMethod(handle, "isSameNode", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.IsSameNode(this, node, in call));

        Realm.DefineMethod(handle, "contains", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.Contains(this, node, in call));

        Realm.DefineMethod(handle, "compareDocumentPosition", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.CompareDocumentPosition(this, node, in call));

        Realm.DefineMethod(handle, "getRootNode", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.GetRootNode(this, node, in call));

        // -- ChildNode mixin --
        // The realm's, like the character-data wrapper's four above.
        Realm.DefineMethod(handle, "remove",
            (in call) => Dom.Features.ChildNodeBinding.Remove(this, node, in call));

        Realm.DefineMethod(handle, "before",
            (in call) => Dom.Features.ChildNodeBinding.Before(this, node, in call));

        Realm.DefineMethod(handle, "after",
            (in call) => Dom.Features.ChildNodeBinding.After(this, node, in call));

        Realm.DefineMethod(handle, "replaceWith",
            (in call) => Dom.Features.ChildNodeBinding.ReplaceWith(this, node, in call));

        // addEventListener / removeEventListener / dispatchEvent are on EventTarget.prototype,
        // routed by receiver (DomBridge/Events.cs) — one function for every target, as
        // in a browser. A wrapper minted before the realm carried it installs its own, through the
        // realm, exactly as the routed path does.
        if (!_eventTargetRoutingReady)
        {

            Realm.DefineMethod(handle, "addEventListener", 3,
                (in call) => Dom.Features.EventTargetBinding.AddEventListener(this, node, in call));

            Realm.DefineMethod(handle, "removeEventListener", 3,
                (in call) => Dom.Features.EventTargetBinding.RemoveEventListener(this, node, in call));

            Realm.DefineMethod(handle, "dispatchEvent", 1,
                (in call) => Dom.Features.EventTargetBinding.DispatchEvent(this, node, in call));
        }

        // Node interface constants (exist on all Node objects) — types and DOCUMENT_POSITION_* bits.
        // On Node.prototype, which this wrapper inherits; one minted before the realm carried it
        // installs its own.
        InstallNodeConstantsIfNotInherited(handle);
    }

    /// <summary>
    /// Builds the JS wrapper for a canonical <see cref="DomDocumentFragment"/>. A
    /// fragment is a non-element container: it gets the Node base + ParentNode mixin + child-
    /// manipulation surface, but NOT the element-only surface (attributes/style/tagName) it inherited
    /// while it was a <c>#document-fragment</c> sentinel element. Node-generic members reuse the same
    /// handlers the character-data wrapper uses; the element views and <c>textContent</c> are the
    /// canonical <see cref="DomNode"/> members an element's wrapper reads too, and the other container
    /// members are focused fragment lambdas over the neutral tree helpers and the (DomNode-widened)
    /// <see cref="InsertNodeAt"/> — a fragment parent has no style scope or sub-document onload.
    /// </summary>
    private void PopulateDocumentFragmentWrapper(JsValue handle, DomDocumentFragment fragment)
    {
        var bridge = this;
        DomNode node = fragment;

        // -- Node identity --
        Realm.DefineAccessor(handle, "nodeType",
            (in call) => Dom.Features.NodeAccessorsBinding.GetNodeType(node, in call), null);
        Realm.DefineAccessor(handle, "nodeName",
            (in call) => Dom.Features.NodeAccessorsBinding.GetNodeName(node, in call), null);
        Realm.DefineAccessor(handle, "nodeValue", (in _) => JsValue.Null, null);
        Realm.DefineAccessor(handle, "ownerDocument",
            (in call) => Dom.Features.NodeAccessorsBinding.GetOwnerDocument(this, node, in call), null);

        // -- Tree navigation --
        Realm.DefineAccessor(handle, "parentNode",
            (in _) => node.ParentNode != null ? WrapNode(node.ParentNode) : JsValue.Null,
            null);
        Realm.DefineAccessor(handle, "parentElement",
            (in call) => Dom.Features.NodeAccessorsBinding.GetParentElement(this, node, in call), null);
        Realm.DefineAccessor(handle, "childNodes",
            (in call) => Dom.Features.NodeAccessorsBinding.GetChildNodes(this, node, in call), null);
        Realm.DefineAccessor(handle, "firstChild",
            (in call) => Dom.Features.NodeAccessorsBinding.GetFirstChild(this, node, in call), null);
        Realm.DefineAccessor(handle, "lastChild",
            (in call) => Dom.Features.NodeAccessorsBinding.GetLastChild(this, node, in call), null);
        Realm.DefineAccessor(handle, "isConnected",
            (in call) => Dom.Features.NodeAccessorsBinding.GetIsConnected(node, in call), null);
        Realm.DefineMethod(handle, "hasChildNodes", (in _) => JsValue.Boolean(fragment.ChildNodes.Count > 0));

        // -- ParentNode mixin (element views) --
        // The same canonical element views an element's wrapper reads (ElementTraversalBinding).
        Realm.DefineAccessor(handle, "children",
            (in _) => Dom.Features.ElementTraversalBinding.GetChildren(this, fragment),
            null);
        Realm.DefineAccessor(handle, "childElementCount",
            (in _) => JsValue.Number(fragment.ChildElementCount),
            null);
        Realm.DefineAccessor(handle, "firstElementChild",
            (in _) => Dom.Features.ElementTraversalBinding.GetFirstElementChild(this, fragment),
            null);
        Realm.DefineAccessor(handle, "lastElementChild",
            (in _) => Dom.Features.ElementTraversalBinding.GetLastElementChild(this, fragment),
            null);

        // -- textContent (get/set) --
        // Both halves are canonical DomNode.TextContent: a fragment's text is its descendants' text, as
        // an element's is (the bridge's own walk answered "" for every fragment), and a write replaces
        // its children through the setter every node kind uses, which passes null through as IDL null.
        Realm.DefineAccessor(handle, "textContent",
            (in _) => JsValue.String(fragment.TextContent),
            (in call) => Dom.Features.NodeAccessorsBinding.SetTextContent(fragment, in call));

        // -- Child manipulation --
        Realm.DefineMethod(handle, "appendChild", 1, (in call) =>
        {
            if (call.Length == 0 || !call[0].IsObject)
                return JsValue.Undefined;
            var childEl = FindDomNodeByJSObject(call[0]);
            if (childEl == null)
                return call[0];
            if (ReferenceEquals(childEl, fragment) || fragment.IsDescendantOf(childEl))
                throw call.Realm.DomError("HierarchyRequestError", "The new child element contains the parent.");
            InsertNodeAt(fragment, childEl, fragment.ChildNodes.Count);
            return call[0];
        });
        Realm.DefineMethod(handle, "insertBefore", 2, (in call) =>
        {
            if (call.Length == 0 || !call[0].IsObject)
                return JsValue.Undefined;
            var newEl = FindDomNodeByJSObject(call[0]);
            if (newEl == null)
                return call[0];
            if (ReferenceEquals(newEl, fragment) || fragment.IsDescendantOf(newEl))
                throw call.Realm.DomError("HierarchyRequestError", "The new child element contains the parent.");
            if (call.Length < 2 || call[1].IsNull || call[1].IsUndefined)
            {
                InsertNodeAt(fragment, newEl, fragment.ChildNodes.Count);
                return call[0];
            }
            if (!call[1].IsObject)
                return call[0];
            var refEl = FindDomNodeByJSObject(call[1]);
            if (refEl == null || ReferenceEquals(newEl, refEl))
                return call[0];
            var idx = ChildIndexOf(fragment, refEl);
            if (idx < 0)
                throw call.Realm.Error(JsErrorKind.Error, "NotFoundError: The node before which the new node is to be inserted is not a child of this node.");
            InsertNodeAt(fragment, newEl, idx);
            return call[0];
        });
        Realm.DefineMethod(handle, "removeChild", 1, (in call) =>
        {
            if (call.Length == 0 || !call[0].IsObject)
                return JsValue.Undefined;
            var childEl = FindDomNodeByJSObject(call[0]);
            if (childEl == null)
                return call[0];
            var idx = ChildIndexOf(fragment, childEl);
            if (idx < 0)
                return call[0];
            RemoveNthChild(fragment, idx);
            SetParent(childEl, null);
            return call[0];
        });
        Realm.DefineMethod(handle, "replaceChild", 2, (in call) =>
        {
            if (call.Length < 2 || !call[0].IsObject || !call[1].IsObject)
                return JsValue.Undefined;
            var newEl = FindDomNodeByJSObject(call[0]);
            var oldEl = FindDomNodeByJSObject(call[1]);
            if (newEl == null || oldEl == null)
                return call[1];
            var idx = ChildIndexOf(fragment, oldEl);
            if (idx < 0)
                return call[1];
            SetParent(oldEl, null);
            InsertNodeAt(fragment, newEl, Math.Min(idx, fragment.ChildNodes.Count));
            return call[1];
        });
        // append/prepend read the whole variadic list — nodes and strings alike — through the bridge's
        // ISubDocumentHost reading of it, which every other host's BuildChildNodeArgumentNodes forwards
        // to and which coerces each non-node argument with the realm's ToString.
        Realm.DefineMethod(handle, "append", (in call) =>
        {
            if (call.Length == 0)
                return JsValue.Undefined;
            var nodes = ((Dom.Features.ISubDocumentHost)this).BuildChildNodeArgumentNodes(call.Arguments);
            var insertIndex = fragment.ChildNodes.Count;
            foreach (var child in nodes)
                InsertNodeAt(fragment, child, insertIndex++);
            return JsValue.Undefined;
        });
        Realm.DefineMethod(handle, "prepend", (in call) =>
        {
            if (call.Length == 0)
                return JsValue.Undefined;
            var nodes = ((Dom.Features.ISubDocumentHost)this).BuildChildNodeArgumentNodes(call.Arguments);
            var insertIndex = 0;
            foreach (var child in nodes)
                InsertNodeAt(fragment, child, insertIndex++);
            return JsValue.Undefined;
        });

        // -- Query --
        // The descendant search answers a handle — a wrapper, a static NodeList, or null — and
        // FromEngineResult, the object-or-null filter the element forms share, passes it through as it
        // is. The selector is read with the realm's ToString, so a selector object runs its own
        // toString.
        Realm.DefineMethod(handle, "querySelector", 1,
            (in call) => FromEngineResult(FindInDescendants(
                fragment, call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty, false, bridge)));
        Realm.DefineMethod(handle, "querySelectorAll", 1,
            (in call) => FromEngineResult(FindInDescendants(
                fragment, call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty, true, bridge)));

        // -- Node methods --
        Realm.DefineMethod(handle, "cloneNode", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.CloneNode(this, node, in call));
        Realm.DefineMethod(handle, "isEqualNode", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.IsEqualNode(this, node, in call));
        Realm.DefineMethod(handle, "isSameNode", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.IsSameNode(this, node, in call));
        Realm.DefineMethod(handle, "contains", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.Contains(this, node, in call));
        Realm.DefineMethod(handle, "compareDocumentPosition", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.CompareDocumentPosition(this, node, in call));
        Realm.DefineMethod(handle, "getRootNode", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.GetRootNode(this, node, in call));
        Realm.DefineMethod(handle, "normalize", 0,
            (in call) => Dom.Features.NodeRelationshipsBinding.Normalize(this, node, in call));

        // -- EventTarget --
        Realm.DefineMethod(handle, "addEventListener", 3,
            (in call) => Dom.Features.EventTargetBinding.AddEventListener(this, node, in call));
        Realm.DefineMethod(handle, "removeEventListener", 3,
            (in call) => Dom.Features.EventTargetBinding.RemoveEventListener(this, node, in call));
        Realm.DefineMethod(handle, "dispatchEvent", 1,
            (in call) => Dom.Features.EventTargetBinding.DispatchEvent(this, node, in call));

        if (fragment is DomShadowRoot shadowRoot)
        {
            Realm.DefineAccessor(handle, "host", (in _) => WrapNode(shadowRoot.Host), null);
            Realm.DefineAccessor(handle, "mode", (in _) => JsValue.String(shadowRoot.Mode == DomShadowRootMode.Open ? "open" : "closed"), null);
            Realm.DefineAccessor(handle, "delegatesFocus", (in _) => JsValue.Boolean(shadowRoot.DelegatesFocus), null);
            Realm.DefineAccessor(handle, "slotAssignment", (in _) => JsValue.String(shadowRoot.SlotAssignment == DomSlotAssignmentMode.Manual ? "manual" : "named"), null);
            Realm.DefineAccessor(handle, "innerHTML",
                (in _) => JsValue.String(SerializeChildrenToHtml(shadowRoot)),
                (in call) =>
                {
                    var html = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
                    SetShadowRootInnerHtml(shadowRoot, html);
                    return JsValue.Undefined;
                });
        }

        // Node interface constants (exist on all Node objects) — types and DOCUMENT_POSITION_* bits.
        // On Node.prototype, which this wrapper inherits; one minted before the realm carried it
        // installs its own.
        InstallNodeConstantsIfNotInherited(handle);
    }
}
