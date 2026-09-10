using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

/// <summary>
/// <c>Node</c>, <c>CharacterData</c> and <c>Text</c> as real interfaces for a character-data node:
/// their members on the interface prototypes rather than copied onto every text and comment wrapper.
/// </summary>
/// <remarks>
/// <para>
/// Every DOM wrapper in this bridge installs its interface as own properties of each object, so
/// <c>Object.getOwnPropertyNames(node)</c> lists the whole interface and
/// <c>Text.prototype.splitText</c> is <see langword="undefined"/> — track 6's wrapper item. The
/// prototype <em>chain</em> has been real since <see cref="ApplyInterfacePrototype"/>
/// (<c>Text → CharacterData → Node → EventTarget → Object</c>), and the interface objects exist; what
/// had not happened is the engine putting its members on them. A text node carried 57 own properties
/// where a browser gives it none.
/// </para>
/// <para>
/// This is the first node interface to move, and the mechanism it needs is the general one:
/// a member on a prototype has no node captured in a closure, so it finds one from its receiver
/// (<see cref="RequireNode"/>, over the registry's constant-time reverse map). That is also what
/// makes an illegal invocation — <c>Text.prototype.splitText.call({}, 1)</c> — a <c>TypeError</c>
/// rather than a crash or a silent wrong answer. <c>Range</c>, <c>Selection</c> and <c>Blob</c> are
/// the same shape with their state in a weak table; a node's state is the node, so the registry that
/// already owns wrapper identity is the table.
/// </para>
/// <para>
/// <b>The split across the three prototypes is Web IDL's, not a convenience.</b> The tree accessors,
/// the node methods and the <c>ChildNode</c> mixin members go on <c>Node.prototype</c> and
/// <c>CharacterData.prototype</c> where the specification puts them, so a page walking a prototype's
/// own property names reads the shape a browser has. <c>splitText</c> is <c>Text</c>'s alone, which
/// is why the old wrapper installed it behind an <c>IsText</c> test and why a <c>Comment</c> must not
/// inherit it.
/// </para>
/// <para>
/// <b>An element inherits the <c>Node.prototype</c> members installed here too.</b> It shadowed
/// every one with a byte-identical copy of its own; those copies are gone, so the prototype is where
/// they live for an element as well — see <c>PopulateElementNodeMembersOnInstance</c>, which is now
/// only the pre-realm fallback. <c>textContent</c> is the exception and stays the element's own: an
/// element's is a different operation from a character-data node's.
/// </para>
/// <para>
/// A document still keeps its own. Its <c>Node</c> members are separate implementations rather than
/// copies — <c>nodeType</c> is a literal <c>9</c>, <c>childNodes</c> a different binding — so each
/// has to be checked against the prototype's answer rather than deleted, which is its own piece of
/// work. The rest of <c>Element</c>'s surface is the larger remainder.
/// </para>
/// <para>
/// <b>The three <c>EventTarget</c> members are not here, and not on the instance either.</b> They
/// stayed on the wrapper when this moved, because the realm's own <c>EventTarget.prototype</c> keeps
/// its listeners engine-side where the bridge's dispatch would never find them — so a node could not
/// simply inherit them, and shadowing them on <c>Node.prototype</c> would have put three members on a
/// prototype no browser carries them on. That is resolved where it belongs, on
/// <c>EventTarget.prototype</c> itself: see <c>DomBridge.EventTargetInterface.cs</c>, which routes
/// those three by receiver. A text or comment node consequently carries no own properties at all.
/// </para>
/// <para>
/// <b>One vocabulary for installing a prototype member.</b> <see cref="DefinePrototypeMethod"/> and
/// <see cref="DefinePrototypeAccessor"/> mint through the realm, and every member below uses them —
/// the <c>ChildNode</c> mixin four included — because every body below calls a binding that reads a
/// <see cref="JsCall"/> frame. An engine-typed pair stood beside them for
/// <c>DomBridge/ElementInterface.cs</c> and <c>DomBridge/HtmlElementInterface.cs</c>, whose bodies
/// took the engine's argument frame; those two files install through the realm now, and the one
/// engine-framed helper they still need for the four members whose <em>modules</em> have not
/// migrated is declared where those four are, in <c>DomBridge/ElementInterface.cs</c>. Nothing here
/// names an engine type.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>
    /// Whether the node interface prototypes carry their members yet, which is what lets a wrapper
    /// stop installing them — a character-data wrapper its whole interface, an element the <c>Node</c>
    /// members it used to duplicate.
    /// </summary>
    /// <remarks>
    /// A wrapper minted before the realm is up has no prototype to inherit from —
    /// <see cref="ApplyInterfacePrototype"/> is a no-op then — so it still installs its own members,
    /// exactly as before. Without that fallback such a node would have neither, and the shape it gets
    /// is the old one rather than a broken one.
    /// </remarks>
    private bool _nodeInterfacePrototypesReady;

    /// <summary>
    /// Installs the <c>Node</c>, <c>CharacterData</c> and <c>Text</c> members a character-data node
    /// exposes onto their interface prototypes. A no-op when the realm does not carry the interfaces.
    /// </summary>
    internal void RegisterCharacterDataInterface()
    {
        // Asked for one at a time, so that a realm missing `Node` never looks the other two up — the
        // short-circuit the `is not { } … || …` chain this replaces performed.
        var nodeProto = PrototypeHandleOfInterface("Node");
        if (!nodeProto.IsObject)
            return;

        var characterDataProto = PrototypeHandleOfInterface("CharacterData");
        if (!characterDataProto.IsObject)
            return;

        var textProto = PrototypeHandleOfInterface("Text");
        if (!textProto.IsObject)
            return;

        InstallNodePrototypeMembers(nodeProto);
        InstallCharacterDataPrototypeMembers(characterDataProto);
        InstallElementNamePrototypeMembers();

        // Text's alone: a Comment inherits CharacterData and must not answer splitText.
        DefinePrototypeMethod(textProto, "splitText", 1,
            (in call) => Dom.Features.CharacterDataBinding.SplitText(
                this, RequireNode(in call, "Text", "splitText"), in call));

        _nodeInterfacePrototypesReady = true;

        DropDocumentNodeMemberCopies();
    }

    /// <summary>
    /// The <c>Node</c> members and constants the <c>document</c> wrapper installed for itself, dropped
    /// now that <c>Node.prototype</c> carries them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every other wrapper is minted lazily and simply skips installing what it can inherit. The
    /// document's is not: it is built during document registration, which runs before the interface
    /// constructors this pass needs exist, so by the time there is a prototype to inherit from it has
    /// already made its own copies. Removing them afterwards is what makes the ordering irrelevant,
    /// short of reordering registration itself.
    /// </para>
    /// <para>
    /// Only the five members it actually had, and only after checking that the prototype answers the
    /// same for a document receiver: <c>nodeType</c> is 9, <c>nodeName</c> is <c>#document</c>,
    /// <c>childNodes</c>/<c>firstChild</c>/<c>lastChild</c> report the same nodes. They were separate
    /// implementations rather than copies of the prototype's — a literal <c>9</c>, a different
    /// <c>childNodes</c> binding — so agreeing was a thing to verify rather than assume.
    /// </para>
    /// <para>
    /// The eighteen constants beside them need no such check: they are plain numbers, and
    /// <c>RegisterNodeConstructor</c> puts the same eighteen values on <c>Node.prototype</c> — the
    /// copies were duplication rather than a second implementation.
    /// </para>
    /// </remarks>
    private void DropDocumentNodeMemberCopies()
    {
        // The document wrapper's field is still an engine object held by DomBridge.cs; DocumentHandle
        // is that same object asked for as a handle, so deleting through the realm deletes from what
        // the page holds. Missing — no document registered yet — is not an object, which is the same
        // escape the null test on the field made.
        var handle = DocumentHandle;
        if (!handle.IsObject)
            return;

        foreach (var member in new[] { "nodeType", "nodeName", "childNodes", "firstChild", "lastChild" })
            Realm.DeleteProperty(handle, member);

        foreach (var constant in Dom.Features.NodeConstantsBinding.Names)
            Realm.DeleteProperty(handle, constant);
    }

    /// <summary>
    /// The <c>Node</c> constants for a wrapper that cannot inherit them — one minted before the realm
    /// carried the interfaces. Every other wrapper's chain reaches <c>Node.prototype</c>, which has
    /// all eighteen.
    /// </summary>
    private void InstallNodeConstantsIfNotInherited(JsValue handle)
    {
        if (!_nodeInterfacePrototypesReady)
            Dom.Features.NodeConstantsBinding.Install(Realm, handle);
    }

    /// <summary>
    /// The prototype object of a registered interface global as a JSEAL handle, or
    /// <see cref="JsValue.Undefined"/> when the realm carries no such interface.
    /// </summary>
    /// <remarks>
    /// The same two property reads <see cref="PrototypeOfInterface"/> makes, through the realm rather
    /// than through the context — <c>Realm.Global</c> <em>is</em> that context under the Broiler.JS
    /// provider, so this asks the same object the same question.
    /// </remarks>
    private JsValue PrototypeHandleOfInterface(string interfaceName)
    {
        var constructor = Realm.GetProperty(Realm.Global, interfaceName);
        if (!constructor.IsObject)
            return JsValue.Undefined;

        var prototype = Realm.GetProperty(constructor, "prototype");
        return prototype.IsObject ? prototype : JsValue.Undefined;
    }

    /// <summary>
    /// <c>Node.prototype</c>: the tree accessors and node operations. Installed for every node kind,
    /// though only character-data wrappers read them today — an element or document shadows each one
    /// with its own copy until it is migrated too.
    /// </summary>
    private void InstallNodePrototypeMembers(JsValue proto)
    {
        DefinePrototypeAccessor(proto, "nodeType",
            (in call) => Dom.Features.NodeAccessorsBinding.GetNodeType(RequireNode(in call, "Node", "nodeType"), in call));
        DefinePrototypeAccessor(proto, "nodeName",
            (in call) => Dom.Features.NodeAccessorsBinding.GetNodeName(RequireNode(in call, "Node", "nodeName"), in call));

        DefinePrototypeAccessor(proto, "nodeValue",
            (in call) => Dom.Features.NodeAccessorsBinding.GetNodeValue(RequireNode(in call, "Node", "nodeValue"), in call),
            (in call) => Dom.Features.NodeAccessorsBinding.SetNodeValue(this, RequireNode(in call, "Node", "nodeValue"), in call));
        DefinePrototypeAccessor(proto, "textContent",
            // JsValue.String turns the "no text at all" null into JavaScript null, which is the
            // distinction DOM §4.4 draws for a document and a doctype — the same value the engine-typed
            // GetNodeTextValue adapter produces for the sites that still take an engine value.
            (in call) => JsValue.String(NodeTextOrNull(RequireNode(in call, "Node", "textContent"))),
            (in call) => Dom.Features.NodeAccessorsBinding.SetNodeValue(this, RequireNode(in call, "Node", "textContent"), in call));

        DefinePrototypeAccessor(proto, "parentNode", (in call) =>
        {
            var node = RequireNode(in call, "Node", "parentNode");
            return node.ParentNode != null ? WrapNode(node.ParentNode) : JsValue.Null;
        });
        DefinePrototypeAccessor(proto, "parentElement",
            (in call) => Dom.Features.NodeAccessorsBinding.GetParentElement(this, RequireNode(in call, "Node", "parentElement"), in call));
        DefinePrototypeAccessor(proto, "isConnected",
            (in call) => Dom.Features.NodeAccessorsBinding.GetIsConnected(this, RequireNode(in call, "Node", "isConnected"), in call));
        DefinePrototypeAccessor(proto, "childNodes",
            (in call) => Dom.Features.NodeAccessorsBinding.GetChildNodes(this, RequireNode(in call, "Node", "childNodes"), in call));
        DefinePrototypeAccessor(proto, "firstChild",
            (in call) => Dom.Features.NodeAccessorsBinding.GetFirstChild(this, RequireNode(in call, "Node", "firstChild"), in call));
        DefinePrototypeAccessor(proto, "lastChild",
            (in call) => Dom.Features.NodeAccessorsBinding.GetLastChild(this, RequireNode(in call, "Node", "lastChild"), in call));
        DefinePrototypeAccessor(proto, "nextSibling",
            (in call) => Dom.Features.NodeAccessorsBinding.GetNextSibling(this, RequireNode(in call, "Node", "nextSibling"), in call));
        DefinePrototypeAccessor(proto, "previousSibling",
            (in call) => Dom.Features.NodeAccessorsBinding.GetPreviousSibling(this, RequireNode(in call, "Node", "previousSibling"), in call));
        DefinePrototypeAccessor(proto, "ownerDocument",
            (in call) => Dom.Features.NodeAccessorsBinding.GetOwnerDocument(this, RequireNode(in call, "Node", "ownerDocument"), in call));

        DefinePrototypeMethod(proto, "hasChildNodes", 0, (in call) =>
            JsValue.Boolean(RequireNode(in call, "Node", "hasChildNodes").ChildNodes.Count > 0));
        DefinePrototypeMethod(proto, "cloneNode", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.CloneNode(this, RequireNode(in call, "Node", "cloneNode"), in call));
        DefinePrototypeMethod(proto, "contains", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.Contains(this, RequireNode(in call, "Node", "contains"), in call));
        DefinePrototypeMethod(proto, "compareDocumentPosition", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.CompareDocumentPosition(this, RequireNode(in call, "Node", "compareDocumentPosition"), in call));
        DefinePrototypeMethod(proto, "isSameNode", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.IsSameNode(this, RequireNode(in call, "Node", "isSameNode"), in call));
        DefinePrototypeMethod(proto, "isEqualNode", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.IsEqualNode(this, RequireNode(in call, "Node", "isEqualNode"), in call));
        DefinePrototypeMethod(proto, "getRootNode", 1,
            (in call) => Dom.Features.NodeRelationshipsBinding.GetRootNode(this, RequireNode(in call, "Node", "getRootNode"), in call));
        DefinePrototypeMethod(proto, "normalize", 0,
            (in call) => Dom.Features.NodeRelationshipsBinding.Normalize(this, RequireNode(in call, "Node", "normalize"), in call));
    }

    /// <summary>
    /// <c>localName</c>, <c>prefix</c> and <c>namespaceURI</c> on <c>Element.prototype</c>, which is
    /// where the DOM puts them.
    /// </summary>
    /// <remarks>
    /// They are not <c>Node</c> members, though this pass first installed them there: the
    /// character-data wrapper carried all three as own properties, and moving that wrapper's members
    /// wholesale took them along. On <c>Node.prototype</c> they reach every node, so a text node
    /// answered <c>null</c> and — once an element stopped shadowing them — so did the document, where
    /// a browser answers <c>undefined</c> for both because neither interface declares them. DOM §4.9
    /// gives them to <c>Element</c>, and <c>Attr</c> separately; measured in Chromium,
    /// <c>'localName' in Node.prototype</c> is <see langword="false"/> and
    /// <c>Element.prototype</c> owns all three.
    /// </remarks>
    private void InstallElementNamePrototypeMembers()
    {
        var proto = PrototypeHandleOfInterface("Element");
        if (!proto.IsObject)
            return;

        DefinePrototypeAccessor(proto, "localName",
            (in call) => Dom.Features.NodeAccessorsBinding.GetLocalName(RequireNode(in call, "Element", "localName"), in call));
        DefinePrototypeAccessor(proto, "prefix",
            (in call) => Dom.Features.NodeAccessorsBinding.GetPrefix(RequireNode(in call, "Element", "prefix"), in call));
        DefinePrototypeAccessor(proto, "namespaceURI",
            (in call) => Dom.Features.NodeAccessorsBinding.GetNamespaceURI(RequireNode(in call, "Element", "namespaceURI"), in call));
    }

    /// <summary>
    /// <c>CharacterData.prototype</c>: the data operations, plus the <c>ChildNode</c> mixin members —
    /// which the mixin gives to <c>CharacterData</c>, <c>Element</c> and <c>DocumentType</c>
    /// separately, so they belong here rather than on <c>Node.prototype</c>.
    /// </summary>
    /// <remarks>
    /// The four <c>ChildNode</c> members are minted by the realm like the rest: <c>ChildNodeBinding</c>
    /// reads a <see cref="JsCall"/> at the entry point this prototype reaches it through. Their
    /// position in the install order is unchanged, which is what
    /// <c>Object.getOwnPropertyNames(CharacterData.prototype)</c> can see.
    /// </remarks>
    private void InstallCharacterDataPrototypeMembers(JsValue proto)
    {
        DefinePrototypeAccessor(proto, "data",
            (in call) => Dom.Features.CharacterDataBinding.GetData(RequireNode(in call, "CharacterData", "data"), in call),
            (in call) => Dom.Features.CharacterDataBinding.SetData(this, RequireNode(in call, "CharacterData", "data"), in call));
        DefinePrototypeAccessor(proto, "length",
            (in call) => Dom.Features.CharacterDataBinding.GetLength(RequireNode(in call, "CharacterData", "length"), in call));

        DefinePrototypeMethod(proto, "substringData", 2,
            (in call) => Dom.Features.CharacterDataBinding.SubstringData(this, RequireNode(in call, "CharacterData", "substringData"), in call));
        DefinePrototypeMethod(proto, "appendData", 1,
            (in call) => Dom.Features.CharacterDataBinding.AppendData(this, RequireNode(in call, "CharacterData", "appendData"), in call));
        DefinePrototypeMethod(proto, "deleteData", 2,
            (in call) => Dom.Features.CharacterDataBinding.DeleteData(this, RequireNode(in call, "CharacterData", "deleteData"), in call));
        DefinePrototypeMethod(proto, "insertData", 2,
            (in call) => Dom.Features.CharacterDataBinding.InsertData(this, RequireNode(in call, "CharacterData", "insertData"), in call));
        DefinePrototypeMethod(proto, "replaceData", 3,
            (in call) => Dom.Features.CharacterDataBinding.ReplaceData(this, RequireNode(in call, "CharacterData", "replaceData"), in call));

        DefinePrototypeMethod(proto, "remove", 0,
            (in call) => Dom.Features.ChildNodeBinding.Remove(this, RequireNode(in call, "CharacterData", "remove"), in call));
        DefinePrototypeMethod(proto, "before", 0,
            (in call) => Dom.Features.ChildNodeBinding.Before(this, RequireNode(in call, "CharacterData", "before"), in call));
        DefinePrototypeMethod(proto, "after", 0,
            (in call) => Dom.Features.ChildNodeBinding.After(this, RequireNode(in call, "CharacterData", "after"), in call));
        DefinePrototypeMethod(proto, "replaceWith", 0,
            (in call) => Dom.Features.ChildNodeBinding.ReplaceWith(this, RequireNode(in call, "CharacterData", "replaceWith"), in call));
    }

    /// <summary>
    /// The node a prototype member was called on, or a <c>TypeError</c> naming the interface and the
    /// member when the receiver is not a node wrapper — which is what a browser answers for
    /// <c>Text.prototype.splitText.call({}, 1)</c>.
    /// </summary>
    private DomNode RequireNode(in JsCall call, string interfaceName, string member)
    {
        // The reverse map is keyed on the engine object, which an object handle carries; a non-object
        // receiver answers no node without asking, which is the branch the engine-object test took.
        if (call.This.IsObject &&
            _jsObjects.TryGetNode(call.This, out var node))
        {
            return node;
        }

        throw call.Realm.Error(
            JsErrorKind.TypeError,
            $"Failed to execute '{member}' on '{interfaceName}': Illegal invocation");
    }

    /// <summary>Adds a WebIDL operation to an interface prototype, through the realm.</summary>
    /// <remarks>
    /// <see cref="JsPropertyFlags.Default"/> is enumerable, configurable and writable — what the
    /// instance properties were and what Web IDL asks for on a prototype; keeping the same attributes
    /// means only the *location* of the member changes.
    /// </remarks>
    private void DefinePrototypeMethod(JsValue proto, string name, int length, JsNativeFunction body) =>
        Realm.DefineValue(proto, name, Realm.NewMethod(name, body, length));

    /// <summary>Adds a WebIDL attribute to an interface prototype, read-only unless a setter is given.</summary>
    /// <remarks>
    /// A null <paramref name="setter"/> is how a read-only IDL attribute is spelled, and the realm
    /// names the pair <c>get name</c>/<c>set name</c> — the names the engine-typed pair below gave
    /// them explicitly.
    /// </remarks>
    private void DefinePrototypeAccessor(JsValue proto, string name,
        JsNativeFunction getter, JsNativeFunction? setter = null) =>
        Realm.DefineAccessor(proto, name, getter, setter);
}
