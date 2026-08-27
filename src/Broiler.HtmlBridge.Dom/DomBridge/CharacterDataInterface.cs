using Broiler.Dom;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

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
/// (<see cref="NodeFromReceiver"/>, over the registry's constant-time reverse map). That is also what
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
        if (PrototypeOfInterface("Node") is not { } nodeProto ||
            PrototypeOfInterface("CharacterData") is not { } characterDataProto ||
            PrototypeOfInterface("Text") is not { } textProto)
        {
            return;
        }

        InstallNodePrototypeMembers(nodeProto);
        InstallCharacterDataPrototypeMembers(characterDataProto);
        InstallElementNamePrototypeMembers();

        // Text's alone: a Comment inherits CharacterData and must not answer splitText.
        AddPrototypeMethod(textProto, "splitText", 1,
            (in Arguments a) => Dom.Features.CharacterDataBinding.SplitText(
                this, RequireNode(in a, "Text", "splitText"), in a));

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
        if (_documentJSObject is not { } document)
            return;

        foreach (var member in new[] { "nodeType", "nodeName", "childNodes", "firstChild", "lastChild" })
            document.Delete((KeyString)member);

        foreach (var constant in Dom.Features.NodeConstantsBinding.Names)
            document.Delete((KeyString)constant);
    }

    /// <summary>
    /// The <c>Node</c> constants for a wrapper that cannot inherit them — one minted before the realm
    /// carried the interfaces. Every other wrapper's chain reaches <c>Node.prototype</c>, which has
    /// all eighteen.
    /// </summary>
    private void InstallNodeConstantsIfNotInherited(JSObject obj)
    {
        if (!_nodeInterfacePrototypesReady)
            Dom.Features.NodeConstantsBinding.Install(obj);
    }

    /// <summary>The prototype object of a registered interface global, if the realm has one.</summary>
    private JSObject? PrototypeOfInterface(string interfaceName) =>
        _jsContext?[interfaceName] is JSObject constructor
            ? constructor[(KeyString)"prototype"] as JSObject
            : null;

    /// <summary>
    /// <c>Node.prototype</c>: the tree accessors and node operations. Installed for every node kind,
    /// though only character-data wrappers read them today — an element or document shadows each one
    /// with its own copy until it is migrated too.
    /// </summary>
    private void InstallNodePrototypeMembers(JSObject proto)
    {
        AddPrototypeAccessor(proto, "nodeType",
            (in Arguments a) => Dom.Features.NodeAccessorsBinding.GetNodeType(RequireNode(in a, "Node", "nodeType"), in a));
        AddPrototypeAccessor(proto, "nodeName",
            (in Arguments a) => Dom.Features.NodeAccessorsBinding.GetNodeName(RequireNode(in a, "Node", "nodeName"), in a));

        AddPrototypeAccessor(proto, "nodeValue",
            (in Arguments a) => Dom.Features.NodeAccessorsBinding.GetNodeValue(RequireNode(in a, "Node", "nodeValue"), in a),
            (in Arguments a) => Dom.Features.NodeAccessorsBinding.SetNodeValue(this, RequireNode(in a, "Node", "nodeValue"), in a));
        AddPrototypeAccessor(proto, "textContent",
            (in Arguments a) => GetNodeTextValue(RequireNode(in a, "Node", "textContent")),
            (in Arguments a) => Dom.Features.NodeAccessorsBinding.SetNodeValue(this, RequireNode(in a, "Node", "textContent"), in a));

        AddPrototypeAccessor(proto, "parentNode", (in Arguments a) =>
        {
            var node = RequireNode(in a, "Node", "parentNode");
            return node.ParentNode != null ? ToJSObject(node.ParentNode) : JSNull.Value;
        });
        AddPrototypeAccessor(proto, "parentElement",
            (in Arguments a) => Dom.Features.NodeAccessorsBinding.GetParentElement(this, RequireNode(in a, "Node", "parentElement"), in a));
        AddPrototypeAccessor(proto, "isConnected",
            (in Arguments a) => Dom.Features.NodeAccessorsBinding.GetIsConnected(this, RequireNode(in a, "Node", "isConnected"), in a));
        AddPrototypeAccessor(proto, "childNodes",
            (in Arguments a) => Dom.Features.NodeAccessorsBinding.GetChildNodes(this, RequireNode(in a, "Node", "childNodes"), in a));
        AddPrototypeAccessor(proto, "firstChild",
            (in Arguments a) => Dom.Features.NodeAccessorsBinding.GetFirstChild(this, RequireNode(in a, "Node", "firstChild"), in a));
        AddPrototypeAccessor(proto, "lastChild",
            (in Arguments a) => Dom.Features.NodeAccessorsBinding.GetLastChild(this, RequireNode(in a, "Node", "lastChild"), in a));
        AddPrototypeAccessor(proto, "nextSibling",
            (in Arguments a) => Dom.Features.NodeAccessorsBinding.GetNextSibling(this, RequireNode(in a, "Node", "nextSibling"), in a));
        AddPrototypeAccessor(proto, "previousSibling",
            (in Arguments a) => Dom.Features.NodeAccessorsBinding.GetPreviousSibling(this, RequireNode(in a, "Node", "previousSibling"), in a));
        AddPrototypeAccessor(proto, "ownerDocument",
            (in Arguments a) => Dom.Features.NodeAccessorsBinding.GetOwnerDocument(this, RequireNode(in a, "Node", "ownerDocument"), in a));

        AddPrototypeMethod(proto, "hasChildNodes", 0, (in Arguments a) =>
            RequireNode(in a, "Node", "hasChildNodes").ChildNodes.Count > 0 ? JSBoolean.True : JSBoolean.False);
        AddPrototypeMethod(proto, "cloneNode", 1,
            (in Arguments a) => Dom.Features.NodeRelationshipsBinding.CloneNode(this, RequireNode(in a, "Node", "cloneNode"), in a));
        AddPrototypeMethod(proto, "contains", 1,
            (in Arguments a) => Dom.Features.NodeRelationshipsBinding.Contains(this, RequireNode(in a, "Node", "contains"), in a));
        AddPrototypeMethod(proto, "compareDocumentPosition", 1,
            (in Arguments a) => Dom.Features.NodeRelationshipsBinding.CompareDocumentPosition(this, RequireNode(in a, "Node", "compareDocumentPosition"), in a));
        AddPrototypeMethod(proto, "isSameNode", 1,
            (in Arguments a) => Dom.Features.NodeRelationshipsBinding.IsSameNode(this, RequireNode(in a, "Node", "isSameNode"), in a));
        AddPrototypeMethod(proto, "isEqualNode", 1,
            (in Arguments a) => Dom.Features.NodeRelationshipsBinding.IsEqualNode(this, RequireNode(in a, "Node", "isEqualNode"), in a));
        AddPrototypeMethod(proto, "getRootNode", 1,
            (in Arguments a) => Dom.Features.NodeRelationshipsBinding.GetRootNode(this, RequireNode(in a, "Node", "getRootNode"), in a));
        AddPrototypeMethod(proto, "normalize", 0,
            (in Arguments a) => Dom.Features.NodeRelationshipsBinding.Normalize(this, RequireNode(in a, "Node", "normalize"), in a));
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
        if (PrototypeOfInterface("Element") is not { } proto)
            return;

        AddPrototypeAccessor(proto, "localName",
            (in Arguments a) => Dom.Features.NodeAccessorsBinding.GetLocalName(RequireNode(in a, "Element", "localName"), in a));
        AddPrototypeAccessor(proto, "prefix",
            (in Arguments a) => Dom.Features.NodeAccessorsBinding.GetPrefix(RequireNode(in a, "Element", "prefix"), in a));
        AddPrototypeAccessor(proto, "namespaceURI",
            (in Arguments a) => Dom.Features.NodeAccessorsBinding.GetNamespaceURI(RequireNode(in a, "Element", "namespaceURI"), in a));
    }

    /// <summary>
    /// <c>CharacterData.prototype</c>: the data operations, plus the <c>ChildNode</c> mixin members —
    /// which the mixin gives to <c>CharacterData</c>, <c>Element</c> and <c>DocumentType</c>
    /// separately, so they belong here rather than on <c>Node.prototype</c>.
    /// </summary>
    private void InstallCharacterDataPrototypeMembers(JSObject proto)
    {
        AddPrototypeAccessor(proto, "data",
            (in Arguments a) => Dom.Features.CharacterDataBinding.GetData(RequireNode(in a, "CharacterData", "data"), in a),
            (in Arguments a) => Dom.Features.CharacterDataBinding.SetData(this, RequireNode(in a, "CharacterData", "data"), in a));
        AddPrototypeAccessor(proto, "length",
            (in Arguments a) => Dom.Features.CharacterDataBinding.GetLength(RequireNode(in a, "CharacterData", "length"), in a));

        AddPrototypeMethod(proto, "substringData", 2,
            (in Arguments a) => Dom.Features.CharacterDataBinding.SubstringData(this, RequireNode(in a, "CharacterData", "substringData"), in a));
        AddPrototypeMethod(proto, "appendData", 1,
            (in Arguments a) => Dom.Features.CharacterDataBinding.AppendData(this, RequireNode(in a, "CharacterData", "appendData"), in a));
        AddPrototypeMethod(proto, "deleteData", 2,
            (in Arguments a) => Dom.Features.CharacterDataBinding.DeleteData(this, RequireNode(in a, "CharacterData", "deleteData"), in a));
        AddPrototypeMethod(proto, "insertData", 2,
            (in Arguments a) => Dom.Features.CharacterDataBinding.InsertData(this, RequireNode(in a, "CharacterData", "insertData"), in a));
        AddPrototypeMethod(proto, "replaceData", 3,
            (in Arguments a) => Dom.Features.CharacterDataBinding.ReplaceData(this, RequireNode(in a, "CharacterData", "replaceData"), in a));

        AddPrototypeMethod(proto, "remove", 0,
            (in Arguments a) => Dom.Features.ChildNodeBinding.Remove(this, RequireNode(in a, "CharacterData", "remove"), in a));
        AddPrototypeMethod(proto, "before", 0,
            (in Arguments a) => Dom.Features.ChildNodeBinding.Before(this, RequireNode(in a, "CharacterData", "before"), in a));
        AddPrototypeMethod(proto, "after", 0,
            (in Arguments a) => Dom.Features.ChildNodeBinding.After(this, RequireNode(in a, "CharacterData", "after"), in a));
        AddPrototypeMethod(proto, "replaceWith", 0,
            (in Arguments a) => Dom.Features.ChildNodeBinding.ReplaceWith(this, RequireNode(in a, "CharacterData", "replaceWith"), in a));
    }

    /// <summary>
    /// The node a prototype member was called on, or a <c>TypeError</c> naming the interface and the
    /// member when the receiver is not a node wrapper — which is what a browser answers for
    /// <c>Text.prototype.splitText.call({}, 1)</c>.
    /// </summary>
    private DomNode RequireNode(in Arguments a, string interfaceName, string member)
    {
        if (a.This is JSObject receiver && _jsObjects.TryGetNode(receiver, out var node))
            return node;

        return JSException.ThrowTypeError<DomNode>(
            $"Failed to execute '{member}' on '{interfaceName}': Illegal invocation");
    }

    /// <summary>Adds a WebIDL operation to an interface prototype.</summary>
    /// <remarks>
    /// Enumerable and configurable but not writable-as-data is what the instance properties were, and
    /// what Web IDL asks for on a prototype; keeping the same attributes means only the *location* of
    /// the member changes.
    /// </remarks>
    private static void AddPrototypeMethod(JSObject proto, string name, int length, JSFunctionDelegate body) =>
        proto.FastAddValue(name, new DomFunction(body, name, length),
            JSPropertyAttributes.EnumerableConfigurableValue);

    /// <summary>Adds a WebIDL attribute to an interface prototype, read-only unless a setter is given.</summary>
    private static void AddPrototypeAccessor(JSObject proto, string name,
        JSFunctionDelegate getter, JSFunctionDelegate? setter = null) =>
        proto.FastAddProperty(name,
            new DomFunction(getter, "get " + name),
            setter is null ? null : new DomFunction(setter, "set " + name),
            JSPropertyAttributes.EnumerableConfigurableProperty);
}
