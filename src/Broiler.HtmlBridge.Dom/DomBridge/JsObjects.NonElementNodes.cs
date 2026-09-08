using Broiler.JavaScript.Runtime;
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
/// minted by the realm, and so now is everything <c>NodeAccessorsBinding</c>,
/// <c>CharacterDataBinding</c> and <c>NodeRelationshipsBinding</c> answer: those three modules are
/// migrated, so their bodies have a <see cref="JsCall"/> frame of their own.
/// </para>
/// <para>
/// What is left engine-typed is pinned by its callee, not left behind, and it is down to one thing:
/// <c>EventTargetBinding</c> still takes an engine argument frame, so the three
/// <c>addEventListener</c>/<c>removeEventListener</c>/<c>dispatchEvent</c> members are minted by the
/// engine and each populator unwraps the handle for them alone. There is no adapter between two call
/// frames — only between two object types — so they move when that binding does; the two halves
/// install onto one object, so the wrapper's shape cannot drift while they are apart.
/// </para>
/// <para>
/// <c>ChildNodeBinding</c> and the variadic <c>append</c>/<c>prepend</c> reader have both migrated;
/// <c>FindInDescendants</c> has not, but it answers a <em>value</em> rather than taking a frame, and a
/// value crosses — so the fragment's two selector members are the realm's over an engine-typed search.
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
        // same value the engine-typed GetNodeTextValue adapter produced for this getter.
        Realm.DefineAccessor(handle, "textContent",
            (in _) => JsValue.String(NodeTextOrNull(node)),
            (in call) => Dom.Features.NodeAccessorsBinding.SetNodeValue(this, node, in call));

        Realm.DefineAccessor(handle, "data",
            (in call) => Dom.Features.CharacterDataBinding.GetData(node, in call),
            (in call) => Dom.Features.CharacterDataBinding.SetData(this, node, in call));

        Realm.DefineAccessor(handle, "length",
            (in call) => Dom.Features.CharacterDataBinding.GetLength(node, in call), null);

        // splitText is Text-only (not on Comment).
        if (IsText(node))
        {
            Realm.DefineValue(handle, "splitText",
                Realm.NewMethod("splitText",
                    (in call) => Dom.Features.CharacterDataBinding.SplitText(this, node, in call), 1));
        }

        Realm.DefineValue(handle, "substringData",
            Realm.NewMethod("substringData",
                (in call) => Dom.Features.CharacterDataBinding.SubstringData(this, node, in call), 2));

        Realm.DefineValue(handle, "appendData",
            Realm.NewMethod("appendData",
                (in call) => Dom.Features.CharacterDataBinding.AppendData(this, node, in call), 1));

        Realm.DefineValue(handle, "deleteData",
            Realm.NewMethod("deleteData",
                (in call) => Dom.Features.CharacterDataBinding.DeleteData(this, node, in call), 2));

        Realm.DefineValue(handle, "insertData",
            Realm.NewMethod("insertData",
                (in call) => Dom.Features.CharacterDataBinding.InsertData(this, node, in call), 2));

        Realm.DefineValue(handle, "replaceData",
            Realm.NewMethod("replaceData",
                (in call) => Dom.Features.CharacterDataBinding.ReplaceData(this, node, in call), 3));

        // -- Tree navigation --
        Realm.DefineAccessor(handle, "parentNode",
            (in _) => node.ParentNode != null ? WrapNode(node.ParentNode) : JsValue.Null,
            null);

        Realm.DefineAccessor(handle, "parentElement",
            (in call) => Dom.Features.NodeAccessorsBinding.GetParentElement(this, node, in call), null);

        Realm.DefineAccessor(handle, "isConnected",
            (in call) => Dom.Features.NodeAccessorsBinding.GetIsConnected(this, node, in call), null);

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

        Realm.DefineValue(handle, "hasChildNodes",
            Realm.NewMethod("hasChildNodes", (in _) => JsValue.Boolean(node.ChildNodes.Count > 0)));

        // -- Node methods --
        Realm.DefineValue(handle, "cloneNode",
            Realm.NewMethod("cloneNode",
                (in call) => Dom.Features.NodeRelationshipsBinding.CloneNode(this, node, in call), 1));

        Realm.DefineValue(handle, "contains",
            Realm.NewMethod("contains",
                (in call) => Dom.Features.NodeRelationshipsBinding.Contains(this, node, in call), 1));

        Realm.DefineValue(handle, "compareDocumentPosition",
            Realm.NewMethod("compareDocumentPosition",
                (in call) => Dom.Features.NodeRelationshipsBinding.CompareDocumentPosition(this, node, in call), 1));

        Realm.DefineValue(handle, "isSameNode",
            Realm.NewMethod("isSameNode",
                (in call) => Dom.Features.NodeRelationshipsBinding.IsSameNode(this, node, in call), 1));

        Realm.DefineValue(handle, "isEqualNode",
            Realm.NewMethod("isEqualNode",
                (in call) => Dom.Features.NodeRelationshipsBinding.IsEqualNode(this, node, in call), 1));

        Realm.DefineValue(handle, "getRootNode",
            Realm.NewMethod("getRootNode",
                (in call) => Dom.Features.NodeRelationshipsBinding.GetRootNode(this, node, in call), 1));

        Realm.DefineValue(handle, "normalize",
            Realm.NewMethod("normalize",
                (in call) => Dom.Features.NodeRelationshipsBinding.Normalize(this, node, in call), 0));

        // -- ChildNode mixin --
        // The realm's: ChildNodeBinding reads a JsCall frame at the two entry points this file and
        // DomBridge/CharacterDataInterface.cs reach it through.
        Realm.DefineValue(handle, "remove",
            Realm.NewMethod("remove",
                (in call) => Dom.Features.ChildNodeBinding.Remove(this, node, in call)));

        Realm.DefineValue(handle, "before",
            Realm.NewMethod("before",
                (in call) => Dom.Features.ChildNodeBinding.Before(this, node, in call)));

        Realm.DefineValue(handle, "after",
            Realm.NewMethod("after",
                (in call) => Dom.Features.ChildNodeBinding.After(this, node, in call)));

        Realm.DefineValue(handle, "replaceWith",
            Realm.NewMethod("replaceWith",
                (in call) => Dom.Features.ChildNodeBinding.ReplaceWith(this, node, in call)));


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

        // internalSubset is always null — this parser keeps no subset. It was the bridge's shared
        // NullFunction factory, which mints a constructable engine function; the realm's accessor is
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
            (in call) => Dom.Features.NodeAccessorsBinding.GetIsConnected(this, node, in call), null);

        // A doctype is a leaf: hasChildNodes is constantly false.
        Realm.DefineValue(handle, "hasChildNodes",
            Realm.NewMethod("hasChildNodes", (in _) => JsValue.False));

        // -- Node methods --
        Realm.DefineValue(handle, "cloneNode",
            Realm.NewMethod("cloneNode",
                (in call) => Dom.Features.NodeRelationshipsBinding.CloneNode(this, node, in call), 1));

        Realm.DefineValue(handle, "isEqualNode",
            Realm.NewMethod("isEqualNode",
                (in call) => Dom.Features.NodeRelationshipsBinding.IsEqualNode(this, node, in call), 1));

        Realm.DefineValue(handle, "isSameNode",
            Realm.NewMethod("isSameNode",
                (in call) => Dom.Features.NodeRelationshipsBinding.IsSameNode(this, node, in call), 1));

        Realm.DefineValue(handle, "contains",
            Realm.NewMethod("contains",
                (in call) => Dom.Features.NodeRelationshipsBinding.Contains(this, node, in call), 1));

        Realm.DefineValue(handle, "compareDocumentPosition",
            Realm.NewMethod("compareDocumentPosition",
                (in call) => Dom.Features.NodeRelationshipsBinding.CompareDocumentPosition(this, node, in call), 1));

        Realm.DefineValue(handle, "getRootNode",
            Realm.NewMethod("getRootNode",
                (in call) => Dom.Features.NodeRelationshipsBinding.GetRootNode(this, node, in call), 1));

        // -- ChildNode mixin --
        // The realm's, like the character-data wrapper's four above.
        Realm.DefineValue(handle, "remove",
            Realm.NewMethod("remove",
                (in call) => Dom.Features.ChildNodeBinding.Remove(this, node, in call)));

        Realm.DefineValue(handle, "before",
            Realm.NewMethod("before",
                (in call) => Dom.Features.ChildNodeBinding.Before(this, node, in call)));

        Realm.DefineValue(handle, "after",
            Realm.NewMethod("after",
                (in call) => Dom.Features.ChildNodeBinding.After(this, node, in call)));

        Realm.DefineValue(handle, "replaceWith",
            Realm.NewMethod("replaceWith",
                (in call) => Dom.Features.ChildNodeBinding.ReplaceWith(this, node, in call)));

        // addEventListener / removeEventListener / dispatchEvent are on EventTarget.prototype,
        // routed by receiver (DomBridge.EventTargetInterface.cs) — one function for every target, as
        // in a browser. A wrapper minted before the realm carried it installs its own, and those three
        // are the engine's: EventTargetBinding reads the engine's argument frame. The handle carries
        // this very object, so the two halves install onto one.
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

        // Node interface constants (exist on all Node objects) — types and DOCUMENT_POSITION_* bits.
        // On Node.prototype, which this wrapper inherits; one minted before the realm carried it
        // installs its own.
        InstallNodeConstantsIfNotInherited(handle);
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
            (in call) => Dom.Features.NodeAccessorsBinding.GetIsConnected(this, node, in call), null);
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
        // append/prepend read the whole variadic list — nodes and strings alike — through the bridge's
        // migrated ISubDocumentHost reading of it, which is the same reading the engine-framed
        // BuildChildNodeArgumentNodes performs and coerces each non-node argument with the realm's
        // ToString exactly as `value.ToString()` did.
        Realm.DefineValue(handle, "append",
            Realm.NewMethod("append", (in call) =>
            {
                if (call.Length == 0)
                    return JsValue.Undefined;
                var nodes = ((Dom.Features.ISubDocumentHost)this).BuildChildNodeArgumentNodes(call.Arguments);
                var insertIndex = fragment.ChildNodes.Count;
                foreach (var child in nodes)
                    InsertNodeAt(fragment, child, insertIndex++);
                return JsValue.Undefined;
            }));
        Realm.DefineValue(handle, "prepend",
            Realm.NewMethod("prepend", (in call) =>
            {
                if (call.Length == 0)
                    return JsValue.Undefined;
                var nodes = ((Dom.Features.ISubDocumentHost)this).BuildChildNodeArgumentNodes(call.Arguments);
                var insertIndex = 0;
                foreach (var child in nodes)
                    InsertNodeAt(fragment, child, insertIndex++);
                return JsValue.Undefined;
            }));

        // -- Query --
        // The descendant search still answers an engine value — a wrapper, a NodeList, or the engine's
        // null — because DomBridge/Utilities.cs has not migrated; FromEngineResult is the selectors
        // seam's own handle-over-that pair of arms, so only the search stays engine-typed and the
        // members themselves are the realm's. The selector is read with the realm's ToString, which is
        // what the engine frame's `a[0].ToString()` performed: a selector object runs its own toString.
        Realm.DefineValue(handle, "querySelector",
            Realm.NewMethod("querySelector",
                (in call) => FromEngineResult(FindInDescendants(
                    fragment, call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty, false, bridge)), 1));
        Realm.DefineValue(handle, "querySelectorAll",
            Realm.NewMethod("querySelectorAll",
                (in call) => FromEngineResult(FindInDescendants(
                    fragment, call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty, true, bridge)), 1));

        // -- Node methods --
        Realm.DefineValue(handle, "cloneNode",
            Realm.NewMethod("cloneNode",
                (in call) => Dom.Features.NodeRelationshipsBinding.CloneNode(this, node, in call), 1));
        Realm.DefineValue(handle, "isEqualNode",
            Realm.NewMethod("isEqualNode",
                (in call) => Dom.Features.NodeRelationshipsBinding.IsEqualNode(this, node, in call), 1));
        Realm.DefineValue(handle, "isSameNode",
            Realm.NewMethod("isSameNode",
                (in call) => Dom.Features.NodeRelationshipsBinding.IsSameNode(this, node, in call), 1));
        Realm.DefineValue(handle, "contains",
            Realm.NewMethod("contains",
                (in call) => Dom.Features.NodeRelationshipsBinding.Contains(this, node, in call), 1));
        Realm.DefineValue(handle, "compareDocumentPosition",
            Realm.NewMethod("compareDocumentPosition",
                (in call) => Dom.Features.NodeRelationshipsBinding.CompareDocumentPosition(this, node, in call), 1));
        Realm.DefineValue(handle, "getRootNode",
            Realm.NewMethod("getRootNode",
                (in call) => Dom.Features.NodeRelationshipsBinding.GetRootNode(this, node, in call), 1));
        Realm.DefineValue(handle, "normalize",
            Realm.NewMethod("normalize",
                (in call) => Dom.Features.NodeRelationshipsBinding.Normalize(this, node, in call), 0));

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
        InstallNodeConstantsIfNotInherited(handle);
    }

    /// <summary>
    /// The DOM node a wrapper handle stands for, or <see langword="null"/> when it stands for none.
    /// </summary>
    /// <remarks>
    /// The reverse lookup itself is <c>DomBridge/Utilities.cs</c>'s and is keyed on the engine object,
    /// which a handle carries — so this is one cast, gathered here rather than repeated at each of the
    /// six argument reads in the fragment's child manipulation. A non-object handle answers null
    /// without asking, which is the branch the engine-object guard used to take at each site.
    /// </remarks>
    private DomNode? NodeForWrapper(JsValue value) =>
        value.IsObject ? FindDomNodeByJSObject(Dom.Runtime.JsInterop.ToEngineObject(value)) : null;
}
