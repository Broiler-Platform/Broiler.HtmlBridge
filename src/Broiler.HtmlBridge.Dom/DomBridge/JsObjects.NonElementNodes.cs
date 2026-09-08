using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Storage;
using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

/// <summary>
/// Sibling partial peeled out of <c>JsObjects.cs</c> (Phase 3 ratchet, 2026-07-17) to keep it under
/// the 750-line guard: the non-element node JS-wrapper populators. Builds the minimal JS surface for
/// canonical character-data nodes (<c>DomText</c>/<c>DomComment</c>), <c>DocumentType</c>, and
/// <c>DocumentFragment</c> — the counterparts to the element wrapper that <see cref="WrapNode"/>
/// (still in <c>JsObjects.cs</c>) dispatches to for these node kinds. Pure partial-class
/// relocation — no signature, accessibility, or logic change.
/// </summary>
/// <remarks>
/// <para>
/// <b>Each populator takes the wrapper as a handle, and reaches for the engine object only where a
/// member's body still needs one.</b> Every member whose body reads nothing but the DOM tree — the
/// tree links, the element views, <c>textContent</c>, the fragment's own child manipulation — is
/// minted by the realm. The rest are pinned by their callees: <c>NodeAccessorsBinding</c>,
/// <c>CharacterDataBinding</c>, <c>NodeRelationshipsBinding</c>, <c>ChildNodeBinding</c> and
/// <c>EventTargetBinding</c> each still take an engine argument frame, and there is no adapter
/// between two call frames — only between two object types. Those sites migrate when their callees
/// do; the two halves install onto one object, so the wrapper's shape cannot drift while they are
/// apart.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>
    /// RF-BRIDGE-1c Phase F (F3c): builds the minimal Node/CharacterData JS wrapper for a canonical
    /// <c>DomText</c>/<c>DomComment</c> — the members a character-data node actually exposes (no
    /// tagName/style/attributes/querySelector/form/iframe surface). Populated onto the already-cached
    /// <paramref name="handle"/> (the caller registers it in the <c>JsObjectRegistry</c> before calling, so
    /// re-entrant <see cref="WrapNode"/> lookups resolve). The node-level <c>*Core</c> helpers are the
    /// same ones the element wrapper uses, now widened to <see cref="DomNode"/>.
    /// Includes the ChildNode mixin (remove/before/after/replaceWith) and EventTarget (added once the
    /// tree-mutation helpers were widened in F3c part 2b). This wrapper is dead code until the F3c
    /// construction flip; it does not yet expose <c>surroundContents</c>-style range members that only
    /// apply to elements.
    /// </summary>
    private void PopulateCharacterDataWrapper(JsValue handle, DomNode node)
    {
        // The Node, CharacterData and Text members live on the interface prototypes
        // (DomBridge.CharacterDataInterface.cs), which this wrapper inherits — so there is nothing
        // to install here and Object.getOwnPropertyNames(textNode) is the [] a browser gives.
        //
        // Unless the realm was not up when this wrapper was minted, in which case
        // ApplyInterfacePrototype linked nothing and there is no prototype to inherit from. Then the
        // members go on the instance exactly as they always did, which is the old shape rather than
        // a broken one.
        if (!_nodeInterfacePrototypesReady)
            PopulateCharacterDataMembersOnInstance(handle, node);

        // addEventListener / removeEventListener / dispatchEvent are on EventTarget.prototype,
        // routed by receiver (DomBridge.EventTargetInterface.cs) — one function for every target, as
        // in a browser. A wrapper minted before the realm carried it installs its own.
        if (!_eventTargetRoutingReady)
        {
            var obj = Dom.Runtime.JsInterop.ToEngineObject(handle);

            obj.FastAddValue("addEventListener",
                new DomFunction((in a) => Dom.Features.EventTargetBinding.AddEventListener(this, node, in a), "addEventListener", 3),
                JSPropertyAttributes.EnumerableConfigurableValue);

            obj.FastAddValue("removeEventListener",
                new DomFunction((in a) => Dom.Features.EventTargetBinding.RemoveEventListener(this, node, in a), "removeEventListener", 3),
                JSPropertyAttributes.EnumerableConfigurableValue);

            obj.FastAddValue("dispatchEvent",
                new DomFunction((in a) => Dom.Features.EventTargetBinding.DispatchEvent(this, node, in a), "dispatchEvent", 1),
                JSPropertyAttributes.EnumerableConfigurableValue);
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
        var obj = Dom.Runtime.JsInterop.ToEngineObject(handle);

        // -- Node identity --
        obj.FastAddProperty("nodeType",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetNodeType(node, in a), "get nodeType"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        obj.FastAddProperty("nodeName",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetNodeName(node, in a), "get nodeName"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        obj.FastAddProperty("localName",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetLocalName(node, in a), "get localName"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        obj.FastAddProperty("prefix",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetPrefix(node, in a), "get prefix"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        obj.FastAddProperty("namespaceURI",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetNamespaceURI(node, in a), "get namespaceURI"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // -- Character data --
        obj.FastAddProperty("nodeValue",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetNodeValue(node, in a), "get nodeValue"),
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.SetNodeValue(this, node, in a), "set nodeValue"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // textContent's setter is still NodeAccessorsBinding's, and an accessor is installed as a pair
        // — so the getter stays on the engine's side of the seam with it rather than being split off
        // through a realm-minted half this call could not accept.
        obj.FastAddProperty("textContent",
            new DomFunction((in _) => GetNodeTextValue(node), "get textContent"),
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.SetNodeValue(this, node, in a), "set textContent"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        obj.FastAddProperty("data",
            new DomFunction((in a) => Dom.Features.CharacterDataBinding.GetData(node, in a), "get data"),
            new DomFunction((in a) => Dom.Features.CharacterDataBinding.SetData(this, node, in a), "set data"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        obj.FastAddProperty("length",
            new DomFunction((in a) => Dom.Features.CharacterDataBinding.GetLength(node, in a), "get length"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // splitText is Text-only (not on Comment).
        if (IsText(node))
        {
            obj.FastAddValue("splitText",
                new DomFunction((in a) => Dom.Features.CharacterDataBinding.SplitText(this, node, in a), "splitText", 1),
                JSPropertyAttributes.EnumerableConfigurableValue);
        }

        obj.FastAddValue("substringData",
            new DomFunction((in a) => Dom.Features.CharacterDataBinding.SubstringData(this, node, in a), "substringData", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);

        obj.FastAddValue("appendData",
            new DomFunction((in a) => Dom.Features.CharacterDataBinding.AppendData(this, node, in a), "appendData", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        obj.FastAddValue("deleteData",
            new DomFunction((in a) => Dom.Features.CharacterDataBinding.DeleteData(this, node, in a), "deleteData", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);

        obj.FastAddValue("insertData",
            new DomFunction((in a) => Dom.Features.CharacterDataBinding.InsertData(this, node, in a), "insertData", 2),
            JSPropertyAttributes.EnumerableConfigurableValue);

        obj.FastAddValue("replaceData",
            new DomFunction((in a) => Dom.Features.CharacterDataBinding.ReplaceData(this, node, in a), "replaceData", 3),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // -- Tree navigation --
        Realm.DefineAccessor(handle, "parentNode",
            (in _) => node.ParentNode != null ? WrapNode(node.ParentNode) : JsValue.Null,
            null);

        obj.FastAddProperty("parentElement",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetParentElement(this, node, in a), "get parentElement"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        obj.FastAddProperty("isConnected",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetIsConnected(this, node, in a), "get isConnected"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        obj.FastAddProperty("childNodes",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetChildNodes(this, node, in a), "get childNodes"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        obj.FastAddProperty("firstChild",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetFirstChild(this, node, in a), "get firstChild"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        obj.FastAddProperty("lastChild",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetLastChild(this, node, in a), "get lastChild"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        obj.FastAddProperty("nextSibling",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetNextSibling(this, node, in a), "get nextSibling"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        obj.FastAddProperty("previousSibling",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetPreviousSibling(this, node, in a), "get previousSibling"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        obj.FastAddProperty("ownerDocument",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetOwnerDocument(this, node, in a), "get ownerDocument"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        Realm.DefineValue(handle, "hasChildNodes",
            Realm.NewMethod("hasChildNodes", (in _) => JsValue.Boolean(node.ChildNodes.Count > 0)));

        // -- Node methods --
        obj.FastAddValue("cloneNode",
            new DomFunction((in a) => Dom.Features.NodeRelationshipsBinding.CloneNode(this, node, in a), "cloneNode", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        obj.FastAddValue("contains",
            new DomFunction((in a) => Dom.Features.NodeRelationshipsBinding.Contains(this, node, in a), "contains", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        obj.FastAddValue("compareDocumentPosition",
            new DomFunction((in a) => Dom.Features.NodeRelationshipsBinding.CompareDocumentPosition(this, node, in a), "compareDocumentPosition", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        obj.FastAddValue("isSameNode",
            new DomFunction((in a) => Dom.Features.NodeRelationshipsBinding.IsSameNode(this, node, in a), "isSameNode", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        obj.FastAddValue("isEqualNode",
            new DomFunction((in a) => Dom.Features.NodeRelationshipsBinding.IsEqualNode(this, node, in a), "isEqualNode", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        obj.FastAddValue("getRootNode",
            new DomFunction((in a) => Dom.Features.NodeRelationshipsBinding.GetRootNode(this, node, in a), "getRootNode", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        obj.FastAddValue("normalize",
            new DomFunction((in a) => Dom.Features.NodeRelationshipsBinding.Normalize(this, node, in a), "normalize", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // -- ChildNode mixin --
        obj.FastAddValue("remove",
            new DomFunction((in a) => Dom.Features.ChildNodeBinding.Remove(this, node, in a), "remove", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        obj.FastAddValue("before",
            new DomFunction((in a) => Dom.Features.ChildNodeBinding.Before(this, node, in a), "before", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        obj.FastAddValue("after",
            new DomFunction((in a) => Dom.Features.ChildNodeBinding.After(this, node, in a), "after", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        obj.FastAddValue("replaceWith",
            new DomFunction((in a) => Dom.Features.ChildNodeBinding.ReplaceWith(this, node, in a), "replaceWith", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);


        // The constants are on Node.prototype for every other wrapper; this one inherits nothing.
        Dom.Features.NodeConstantsBinding.Install(Realm, handle);
    }

    /// <summary>
    /// Builds the JS wrapper for a canonical <see cref="DomDocumentType"/> node (Phase 4 item 1).
    /// A DocumentType is a leaf Node with the ChildNode mixin and DocumentType-specific
    /// <c>name</c>/<c>publicId</c>/<c>systemId</c>/<c>internalSubset</c> — it deliberately does NOT get
    /// the element surface (attributes/style/children) it inherited while it was a <c>#doctype</c>
    /// sentinel element, nor the CharacterData mutation methods. The node-generic handlers are the
    /// same ones the character-data wrapper uses.
    /// </summary>
    private void PopulateDocumentTypeWrapper(JsValue handle, DomDocumentType doctype)
    {
        var obj = Dom.Runtime.JsInterop.ToEngineObject(handle);
        DomNode node = doctype;

        // -- Node identity --
        obj.FastAddProperty("nodeType",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetNodeType(node, in a), "get nodeType"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        obj.FastAddProperty("nodeName",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetNodeName(node, in a), "get nodeName"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // A doctype has no value and no text: DOM §4.4 gives it null for both, and the realm mints
        // the two read-only accessors.
        Realm.DefineAccessor(handle, "nodeValue", (in _) => JsValue.Null, null);

        Realm.DefineAccessor(handle, "textContent", (in _) => JsValue.Null, null);

        // -- DocumentType interface --
        Realm.DefineAccessor(handle, "name", (in _) => JsValue.String(doctype.Name), null);

        obj.FastAddProperty("publicId",
            new DomFunction((in _) => Dom.Features.NodeAccessorsBinding.GetPublicId(node, in _), "get publicId"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        obj.FastAddProperty("systemId",
            new DomFunction((in _) => Dom.Features.NodeAccessorsBinding.GetSystemId(node, in _), "get systemId"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // internalSubset is always null — this parser keeps no subset. It was the bridge's shared
        // NullFunction factory, which mints a constructable engine function; the realm's accessor is
        // non-constructable, which is what WebIDL says an attribute getter is.
        Realm.DefineAccessor(handle, "internalSubset", (in _) => JsValue.Null, null);

        // -- Tree navigation --
        Realm.DefineAccessor(handle, "parentNode",
            (in _) => node.ParentNode != null ? WrapNode(node.ParentNode) : JsValue.Null,
            null);

        obj.FastAddProperty("parentElement",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetParentElement(this, node, in a), "get parentElement"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        obj.FastAddProperty("previousSibling",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetPreviousSibling(this, node, in a), "get previousSibling"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        obj.FastAddProperty("nextSibling",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetNextSibling(this, node, in a), "get nextSibling"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        obj.FastAddProperty("ownerDocument",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetOwnerDocument(this, node, in a), "get ownerDocument"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        obj.FastAddProperty("isConnected",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetIsConnected(this, node, in a), "get isConnected"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // A doctype is a leaf: hasChildNodes is constantly false.
        Realm.DefineValue(handle, "hasChildNodes",
            Realm.NewMethod("hasChildNodes", (in _) => JsValue.False));

        // -- Node methods --
        obj.FastAddValue("cloneNode",
            new DomFunction((in a) => Dom.Features.NodeRelationshipsBinding.CloneNode(this, node, in a), "cloneNode", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        obj.FastAddValue("isEqualNode",
            new DomFunction((in a) => Dom.Features.NodeRelationshipsBinding.IsEqualNode(this, node, in a), "isEqualNode", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        obj.FastAddValue("isSameNode",
            new DomFunction((in a) => Dom.Features.NodeRelationshipsBinding.IsSameNode(this, node, in a), "isSameNode", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        obj.FastAddValue("contains",
            new DomFunction((in a) => Dom.Features.NodeRelationshipsBinding.Contains(this, node, in a), "contains", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        obj.FastAddValue("compareDocumentPosition",
            new DomFunction((in a) => Dom.Features.NodeRelationshipsBinding.CompareDocumentPosition(this, node, in a), "compareDocumentPosition", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        obj.FastAddValue("getRootNode",
            new DomFunction((in a) => Dom.Features.NodeRelationshipsBinding.GetRootNode(this, node, in a), "getRootNode", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // -- ChildNode mixin --
        obj.FastAddValue("remove",
            new DomFunction((in a) => Dom.Features.ChildNodeBinding.Remove(this, node, in a), "remove", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        obj.FastAddValue("before",
            new DomFunction((in a) => Dom.Features.ChildNodeBinding.Before(this, node, in a), "before", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        obj.FastAddValue("after",
            new DomFunction((in a) => Dom.Features.ChildNodeBinding.After(this, node, in a), "after", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        obj.FastAddValue("replaceWith",
            new DomFunction((in a) => Dom.Features.ChildNodeBinding.ReplaceWith(this, node, in a), "replaceWith", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // addEventListener / removeEventListener / dispatchEvent are on EventTarget.prototype,
        // routed by receiver (DomBridge.EventTargetInterface.cs) — one function for every target, as
        // in a browser. A wrapper minted before the realm carried it installs its own.
        if (!_eventTargetRoutingReady)
        {
            obj.FastAddValue("addEventListener",
                new DomFunction((in a) => Dom.Features.EventTargetBinding.AddEventListener(this, node, in a), "addEventListener", 3),
                JSPropertyAttributes.EnumerableConfigurableValue);

            obj.FastAddValue("removeEventListener",
                new DomFunction((in a) => Dom.Features.EventTargetBinding.RemoveEventListener(this, node, in a), "removeEventListener", 3),
                JSPropertyAttributes.EnumerableConfigurableValue);

            obj.FastAddValue("dispatchEvent",
                new DomFunction((in a) => Dom.Features.EventTargetBinding.DispatchEvent(this, node, in a), "dispatchEvent", 1),
                JSPropertyAttributes.EnumerableConfigurableValue);
        }

        // Node interface constants (exist on all Node objects) — types and DOCUMENT_POSITION_* bits.
        // On Node.prototype, which this wrapper inherits; one minted before the realm carried it
        // installs its own.
        InstallNodeConstantsIfNotInherited(obj);
    }

    /// <summary>
    /// Builds the JS wrapper for a canonical <see cref="DomDocumentFragment"/> (Phase 4 item 1). A
    /// fragment is a non-element container: it gets the Node base + ParentNode mixin + child-
    /// manipulation surface, but NOT the element-only surface (attributes/style/tagName) it inherited
    /// while it was a <c>#document-fragment</c> sentinel element. Node-generic members reuse the same
    /// handlers the character-data wrapper uses; the container members are focused fragment lambdas
    /// over the neutral tree helpers and the (DomNode-widened) <see cref="InsertNodeAt"/> — a fragment
    /// parent has no style scope, sub-document onload or child-mutation-observer side effects.
    /// </summary>
    private void PopulateDocumentFragmentWrapper(JsValue handle, DomDocumentFragment fragment)
    {
        var obj = Dom.Runtime.JsInterop.ToEngineObject(handle);
        var bridge = this;
        DomNode node = fragment;

        // -- Node identity --
        obj.FastAddProperty("nodeType",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetNodeType(node, in a), "get nodeType"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);
        obj.FastAddProperty("nodeName",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetNodeName(node, in a), "get nodeName"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);
        Realm.DefineAccessor(handle, "nodeValue", (in _) => JsValue.Null, null);
        obj.FastAddProperty("ownerDocument",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetOwnerDocument(this, node, in a), "get ownerDocument"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // -- Tree navigation --
        Realm.DefineAccessor(handle, "parentNode",
            (in _) => node.ParentNode != null ? WrapNode(node.ParentNode) : JsValue.Null,
            null);
        obj.FastAddProperty("parentElement",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetParentElement(this, node, in a), "get parentElement"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);
        obj.FastAddProperty("childNodes",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetChildNodes(this, node, in a), "get childNodes"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);
        obj.FastAddProperty("firstChild",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetFirstChild(this, node, in a), "get firstChild"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);
        obj.FastAddProperty("lastChild",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetLastChild(this, node, in a), "get lastChild"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);
        obj.FastAddProperty("isConnected",
            new DomFunction((in a) => Dom.Features.NodeAccessorsBinding.GetIsConnected(this, node, in a), "get isConnected"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);
        Realm.DefineValue(handle, "hasChildNodes",
            Realm.NewMethod("hasChildNodes", (in _) => JsValue.Boolean(fragment.ChildNodes.Count > 0)));

        // -- ParentNode mixin (element views) --
        Realm.DefineAccessor(handle, "children",
            (in _) => Realm.NewArray([.. ChildElements(fragment).Where(c => !IsText(c)).Select(c => WrapNode(c))]),
            null);
        Realm.DefineAccessor(handle, "childElementCount",
            (in _) => JsValue.Number(ChildElements(fragment).Count(c => !IsText(c))),
            null);
        Realm.DefineAccessor(handle, "firstElementChild",
            (in _) =>
            {
                var first = ChildElements(fragment).FirstOrDefault(c => !IsText(c));
                return first != null ? WrapNode(first) : JsValue.Null;
            },
            null);
        Realm.DefineAccessor(handle, "lastElementChild",
            (in _) =>
            {
                var last = ChildElements(fragment).LastOrDefault(c => !IsText(c));
                return last != null ? WrapNode(last) : JsValue.Null;
            },
            null);

        // -- textContent (get/set) --
        Realm.DefineAccessor(handle, "textContent",
            (in _) => JsValue.String(NodeTextOrNull(node)),
            (in call) =>
            {
                ClearChildren(fragment);
                // ToJsString, not the handle's rendering: an object argument must run its own
                // toString, which is the coercion a page observes here.
                var value = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
                if (!string.IsNullOrEmpty(value))
                    fragment.AppendChild(CreateBridgeTextNode(value));
                return JsValue.Undefined;
            });

        // -- Child manipulation --
        Realm.DefineValue(handle, "appendChild",
            Realm.NewMethod("appendChild", (in call) =>
            {
                if (call.Length == 0 || !call[0].IsObject)
                    return JsValue.Undefined;
                var childEl = NodeForWrapper(call[0]);
                if (childEl == null)
                    return call[0];
                if (ReferenceEquals(childEl, fragment) || fragment.IsDescendantOf(childEl))
                    throw call.Realm.DomError("HierarchyRequestError", "The new child element contains the parent.");
                InsertNodeAt(fragment, childEl, fragment.ChildNodes.Count);
                return call[0];
            }, 1));
        Realm.DefineValue(handle, "insertBefore",
            Realm.NewMethod("insertBefore", (in call) =>
            {
                if (call.Length == 0 || !call[0].IsObject)
                    return JsValue.Undefined;
                var newEl = NodeForWrapper(call[0]);
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
                var refEl = NodeForWrapper(call[1]);
                if (refEl == null || ReferenceEquals(newEl, refEl))
                    return call[0];
                var idx = ChildIndexOf(fragment, refEl);
                if (idx < 0)
                    throw call.Realm.Error(JsErrorKind.Error, "NotFoundError: The node before which the new node is to be inserted is not a child of this node.");
                InsertNodeAt(fragment, newEl, idx);
                return call[0];
            }, 2));
        Realm.DefineValue(handle, "removeChild",
            Realm.NewMethod("removeChild", (in call) =>
            {
                if (call.Length == 0 || !call[0].IsObject)
                    return JsValue.Undefined;
                var childEl = NodeForWrapper(call[0]);
                if (childEl == null)
                    return call[0];
                var idx = ChildIndexOf(fragment, childEl);
                if (idx < 0)
                    return call[0];
                NotifyNodeIteratorPreRemoval(childEl);
                RemoveNthChild(fragment, idx);
                SetParent(childEl, null);
                return call[0];
            }, 1));
        Realm.DefineValue(handle, "replaceChild",
            Realm.NewMethod("replaceChild", (in call) =>
            {
                if (call.Length < 2 || !call[0].IsObject || !call[1].IsObject)
                    return JsValue.Undefined;
                var newEl = NodeForWrapper(call[0]);
                var oldEl = NodeForWrapper(call[1]);
                if (newEl == null || oldEl == null)
                    return call[1];
                var idx = ChildIndexOf(fragment, oldEl);
                if (idx < 0)
                    return call[1];
                SetParent(oldEl, null);
                InsertNodeAt(fragment, newEl, Math.Min(idx, fragment.ChildNodes.Count));
                return call[1];
            }, 2));
        // append/prepend stay on the engine's argument frame: BuildChildNodeArgumentNodes reads the
        // whole variadic list — nodes and strings alike — and it takes an `Arguments`. They migrate
        // when it does.
        obj.FastAddValue("append",
            new DomFunction((in a) =>
            {
                if (a.Length == 0)
                    return JSUndefined.Value;
                var nodes = BuildChildNodeArgumentNodes(a);
                var insertIndex = fragment.ChildNodes.Count;
                foreach (var child in nodes)
                    InsertNodeAt(fragment, child, insertIndex++);
                return JSUndefined.Value;
            }, "append", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);
        obj.FastAddValue("prepend",
            new DomFunction((in a) =>
            {
                if (a.Length == 0)
                    return JSUndefined.Value;
                var nodes = BuildChildNodeArgumentNodes(a);
                var insertIndex = 0;
                foreach (var child in nodes)
                    InsertNodeAt(fragment, child, insertIndex++);
                return JSUndefined.Value;
            }, "prepend", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // -- Query --
        // The descendant search answers an engine value (a wrapper, a NodeList, or the engine's null),
        // so these two stay on the engine's frame until DomBridge/Utilities.cs migrates.
        obj.FastAddValue("querySelector",
            new DomFunction((in a) => FindInDescendants(fragment, a.Length > 0 ? a[0].ToString() : string.Empty, false, bridge), "querySelector", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);
        obj.FastAddValue("querySelectorAll",
            new DomFunction((in a) => FindInDescendants(fragment, a.Length > 0 ? a[0].ToString() : string.Empty, true, bridge), "querySelectorAll", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // -- Node methods --
        obj.FastAddValue("cloneNode",
            new DomFunction((in a) => Dom.Features.NodeRelationshipsBinding.CloneNode(this, node, in a), "cloneNode", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);
        obj.FastAddValue("isEqualNode",
            new DomFunction((in a) => Dom.Features.NodeRelationshipsBinding.IsEqualNode(this, node, in a), "isEqualNode", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);
        obj.FastAddValue("isSameNode",
            new DomFunction((in a) => Dom.Features.NodeRelationshipsBinding.IsSameNode(this, node, in a), "isSameNode", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);
        obj.FastAddValue("contains",
            new DomFunction((in a) => Dom.Features.NodeRelationshipsBinding.Contains(this, node, in a), "contains", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);
        obj.FastAddValue("compareDocumentPosition",
            new DomFunction((in a) => Dom.Features.NodeRelationshipsBinding.CompareDocumentPosition(this, node, in a), "compareDocumentPosition", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);
        obj.FastAddValue("getRootNode",
            new DomFunction((in a) => Dom.Features.NodeRelationshipsBinding.GetRootNode(this, node, in a), "getRootNode", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);
        obj.FastAddValue("normalize",
            new DomFunction((in a) => Dom.Features.NodeRelationshipsBinding.Normalize(this, node, in a), "normalize", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // -- EventTarget --
        obj.FastAddValue("addEventListener",
            new DomFunction((in a) => Dom.Features.EventTargetBinding.AddEventListener(this, node, in a), "addEventListener", 3),
            JSPropertyAttributes.EnumerableConfigurableValue);
        obj.FastAddValue("removeEventListener",
            new DomFunction((in a) => Dom.Features.EventTargetBinding.RemoveEventListener(this, node, in a), "removeEventListener", 3),
            JSPropertyAttributes.EnumerableConfigurableValue);
        obj.FastAddValue("dispatchEvent",
            new DomFunction((in a) => Dom.Features.EventTargetBinding.DispatchEvent(this, node, in a), "dispatchEvent", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // Node interface constants (exist on all Node objects) — types and DOCUMENT_POSITION_* bits.
        // On Node.prototype, which this wrapper inherits; one minted before the realm carried it
        // installs its own.
        InstallNodeConstantsIfNotInherited(obj);
    }

    /// <summary>
    /// The DOM node a wrapper handle stands for, or <see langword="null"/> when it stands for none.
    /// </summary>
    /// <remarks>
    /// The reverse lookup itself is <c>DomBridge/Utilities.cs</c>'s and is keyed on the engine object,
    /// which a handle carries — so this is one cast, gathered here rather than repeated at each of the
    /// six argument reads in the fragment's child manipulation. A non-object handle answers null
    /// without asking, which is the branch the <c>is not JSObject</c> guard used to take at each site.
    /// </remarks>
    private DomNode? NodeForWrapper(JsValue value) =>
        value.IsObject ? FindDomNodeByJSObject(Dom.Runtime.JsInterop.ToEngineObject(value)) : null;
}
