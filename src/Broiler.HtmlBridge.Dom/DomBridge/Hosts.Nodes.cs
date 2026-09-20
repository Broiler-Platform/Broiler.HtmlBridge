using System.Collections.Generic;
using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Features;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JSeal;
using static Broiler.HtmlBridge.DomBridgeHostUtils;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IAttributesHost"/>, the narrow contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.AttributesBinding"/> feature module consumes.
/// Each member is an explicit interface
/// implementation, so these cross-cutting seams (CSSOM inline style, the Events inline-handler
/// compiler and the CSS invalidation route) do not widen the public <c>DomBridge</c> surface.
/// </summary>
public sealed partial class DomBridge : IAttributesHost
{
    IJsRealm IAttributesHost.Realm => Realm;

    void IAttributesHost.ApplyStyleAttribute(DomElement element, string value)
    {
        InlineStyle(element).Clear();
        foreach (var kv in ParseStyle(value, reportDrops: true))
            InlineStyle(element)[kv.Key] = kv.Value;
        InvalidateStyleScope(element);
    }

    void IAttributesHost.CompileInlineEventAttribute(DomElement element, string attributeName, string code) =>
        CompileInlineEventAttribute(element, attributeName, code);

    void IAttributesHost.InvalidateStyleScope(DomElement element) => InvalidateStyleScope(element);

    void IAttributesHost.LinkToInterface(JsValue wrapper, string interfaceName) =>
        LinkToInterface(wrapper, interfaceName);
}

// Explicit ICharacterDataHost implementation for the CharacterDataBinding feature module:
// the bridge exposes the notifying character-data setter, the text-node factory and the JS-wrapper
// factory via explicit interface members, so the module never reaches an arbitrary bridge private
// field and the public surface is unchanged.
//
// This contract names no engine type. WrapNode forwards to the bridge's wrapper factory, which
// answers the realm-minted handle; IJsCalls.DomError owns the DOMException plumbing.
public sealed partial class DomBridge : Dom.Features.ICharacterDataHost
{
    void Dom.Features.ICharacterDataHost.SetCharacterData(DomNode node, string? value)
        => SetCharacterData(node, value);

    DomText Dom.Features.ICharacterDataHost.CreateBridgeTextNode(string data)
        => CreateBridgeTextNode(data);

    JsValue Dom.Features.ICharacterDataHost.WrapNode(DomNode node) => WrapNode(node);
}

// Explicit IChildNodeHost implementation for the ChildNodeBinding feature module: the bridge
// exposes the child-node argument builder, the side-effecting insertion primitive and style-scope
// invalidation via explicit interface members, so the module never reaches an arbitrary bridge private
// field and the public surface is unchanged.
//
// The builder appears once: all three of the mixin's installers mint through the realm, so the one
// reading is the bridge's ISubDocumentHost implementation, which coerces each non-node argument with
// the realm's ToString.
public sealed partial class DomBridge : Dom.Features.IChildNodeHost
{
    List<DomNode> Dom.Features.IChildNodeHost.BuildChildNodeArgumentNodes(ReadOnlySpan<JsValue> arguments)
        => ((Dom.Features.ISubDocumentHost)this).BuildChildNodeArgumentNodes(arguments);

    void Dom.Features.IChildNodeHost.InsertNodeAt(DomNode parent, DomNode node, int index)
        => InsertNodeAt(parent, node, index);

    void Dom.Features.IChildNodeHost.InvalidateStyleScope(DomElement anchor)
        => InvalidateStyleScope(anchor);
}

// Explicit INodeAccessorsHost implementation for the NodeAccessorsBinding feature module:
// the bridge exposes the JS-wrapper factory, the live childNodes collection, the document node, the
// notifying character-data setter and the two document-wrapper lookups via explicit interface members,
// so the module reaches no arbitrary bridge private field and the public surface is unchanged.
//
// Nothing here is engine-typed any more: the module speaks JsValue, the NodeList factory takes the
// realm and the same handles, and the wrapper cache is reached through WrapNode. The childNodes
// collection stays in this file rather than going back to the module for the reason ISelectorsHost's
// two HTMLCollection builders stay: a live collection recomputes its contents on every property read,
// and the bridge is where the child list lives.
public sealed partial class DomBridge : Dom.Features.INodeAccessorsHost
{
    JsValue Dom.Features.INodeAccessorsHost.WrapNode(DomNode node) => WrapNode(node);

    JsValue Dom.Features.INodeAccessorsHost.ChildNodeList(DomNode node) =>
        Dom.Features.DomCollectionBinding.NodeList(Realm, () =>
        {
            var children = new List<JsValue>();
            foreach (var child in node.ChildNodes)
                children.Add(WrapNode(child));

            return children;
        });

    DomNode Dom.Features.INodeAccessorsHost.DocumentNode => _document;

    void Dom.Features.INodeAccessorsHost.SetCharacterData(DomNode node, string? value)
        => SetCharacterData(node, value);

    bool Dom.Features.INodeAccessorsHost.TryGetDocumentWrapper(DomNode documentRoot, out JsValue wrapper)
    {
        if (_jsObjects.TryGetDocument(documentRoot, out var document))
        {
            wrapper = document;
            return true;
        }

        wrapper = JsValue.Missing;
        return false;
    }

    // JsValue.Null rather than a nullable handle: `ownerDocument` coalesced the absent wrapper to
    // JavaScript null at its one call site, so answering that here is the same value by a shorter
    // route rather than a new decision. The wrapper itself is the bridge's document root
    // (DomBridge.cs), which holds Missing when there is no document yet — the coalesce is this
    // contract's, not the root's.
    JsValue Dom.Features.INodeAccessorsHost.DocumentWrapper =>
        DocumentHandle is { IsMissing: false } document ? document : JsValue.Null;
}

// Explicit INodeMutationHost implementation for the NodeMutationBinding feature module:
// the bridge exposes the document node, the JS-wrapper factory and reverse lookup, and the child-node
// argument builder via explicit interface members, so the module never reaches an arbitrary bridge
// private field and the public surface is unchanged.
//
// The contract is spelled in JSEAL and so are the two wrapper members behind it, so this file is no
// longer a seam: the factory answers a handle and the reverse lookup takes one. The wrapper the module
// receives, and the one it hands back for a lookup, are the instances the bridge's wrapper tables are
// keyed on — those tables key on JsValue.ObjectIdentity, the reference the handle carries.
public sealed partial class DomBridge : Dom.Features.INodeMutationHost
{
    JsValue Dom.Features.INodeMutationHost.WrapNode(DomNode node) =>
        WrapNode(node);

    DomNode Dom.Features.INodeMutationHost.DocumentNode => _document;

    // A plain forward: the reverse lookup takes the same handle. There is no unwrap left to fail, and
    // a handle that is not an object answers null — which the module's own IsObject guard, at all four
    // of its call sites, still means this never has to do.
    DomNode? Dom.Features.INodeMutationHost.FindDomNode(JsValue wrapper)
        => FindDomNodeByJSObject(wrapper);

    // One reading, not two: the same migrated argument reading the tree-mutation contract forwards to,
    // which coerces each non-node argument with the realm's ToString exactly as the engine frame did.
    List<DomNode> Dom.Features.INodeMutationHost.BuildChildNodeArgumentNodes(ReadOnlySpan<JsValue> arguments)
        => ((Dom.Features.ISubDocumentHost)this).BuildChildNodeArgumentNodes(arguments);
}

// Explicit INodeRelationshipsHost implementation for the NodeRelationshipsBinding feature module:
// the bridge exposes the wrapper→node resolver, the tree-root walk, the character-data-aware
// normalize(), the root-node wrapper factory, the clone and the plain JS-wrapper factory via explicit
// interface members, so the module reaches no arbitrary bridge private field and the public surface is
// unchanged.
//
// Nothing in this file is engine-typed. The module speaks JSEAL and so does everything below it: the
// reverse wrapper lookup takes a handle (DomBridge/Utilities.cs) and ToJSRootNode answers one
// (DomBridge/ShadowDom.cs). The registry both reach keys on JsValue.ObjectIdentity, so wrapper
// identity is the same question either way. IJsCalls.DomError raises the DOMException a document
// clone must throw.
public sealed partial class DomBridge : Dom.Features.INodeRelationshipsHost
{
    DomNode? Dom.Features.INodeRelationshipsHost.FindNode(JsValue wrapper)
        => wrapper.IsObject ? FindDomNodeByJSObject(wrapper) : null;

    DomNode Dom.Features.INodeRelationshipsHost.GetTreeRoot(DomNode node) => GetTreeRoot(node);

    void Dom.Features.INodeRelationshipsHost.NormalizeNode(DomElement element) => NormalizeNode(element);

    // ToJSRootNode answers the handle or JsValue.Null (a document with no wrapper yet) directly.
    JsValue Dom.Features.INodeRelationshipsHost.WrapRootNode(DomNode root) => ToJSRootNode(root);

    DomNode Dom.Features.INodeRelationshipsHost.CloneDomElement(DomNode source, bool deep)
        => CloneDomElement(source, deep);

    JsValue Dom.Features.INodeRelationshipsHost.WrapNode(DomNode node) => WrapNode(node);
}

// Explicit ITreeMutationHost implementation for the TreeMutationBinding feature module: the
// bridge exposes the wrapper→node resolver, the child-node argument builder, the side-effecting
// insertion primitive and style-scope invalidation via explicit interface members, so the module never
// reaches an arbitrary bridge private field and the public surface is unchanged.
//
// The contract names no engine type. Both installers of these eight members — ElementInterface.cs for
// the ParentNode three, JsObjects.cs for the Node five — mint through the realm.
//
// FindNode's body is a plain forward: the bridge's reverse lookup takes the same handle this member
// is handed. The lookup's name still carries the engine type it once took; the reverse wrapper map it
// reads is keyed on JsValue.ObjectIdentity.
public sealed partial class DomBridge : Dom.Features.ITreeMutationHost
{
    DomNode? Dom.Features.ITreeMutationHost.FindNode(JsValue wrapper)
        => wrapper.IsObject ? FindDomNodeByJSObject(wrapper) : null;

    // One reading, not two: the argument list is read by the bridge's own ISubDocumentHost member,
    // which coerces each non-node argument with the realm's ToString. Forwarding is how the two
    // contracts share that one reading rather than each carrying a copy.
    List<DomNode> Dom.Features.ITreeMutationHost.BuildChildNodeArgumentNodes(ReadOnlySpan<JsValue> arguments)
        => ((Dom.Features.ISubDocumentHost)this).BuildChildNodeArgumentNodes(arguments);

    void Dom.Features.ITreeMutationHost.MoveNodeBefore(DomNode parent, DomNode node, DomNode? reference)
        => MoveNodeBefore(parent, node, reference);

    void Dom.Features.ITreeMutationHost.InsertNodeAt(DomNode parent, DomNode node, int index)
        => InsertNodeAt(parent, node, index);

    void Dom.Features.ITreeMutationHost.InvalidateStyleScope(DomElement anchor)
        => InvalidateStyleScope(anchor);
}

// Explicit IInsertAdjacentHost implementation for the InsertAdjacentBinding feature module: the
// insertAdjacent* methods reach the bridge through this seam — the wrapper reverse lookup, the insertion
// primitive, the text-node factory, the fragment parser, and the computed-style reset — while the neutral
// tree helpers (ParentEl/ChildIndexOf) are internal statics.
//
// The module raises the SyntaxError / NoModificationAllowedError through its own call frame's realm,
// not through DomBridge.ThrowDOMException.
public sealed partial class DomBridge : Dom.Features.IInsertAdjacentHost
{
    // A plain forward: FindDomElementByJSObject takes the same handle, and the wrapper registry behind
    // it is keyed on JsValue.ObjectIdentity. There is no unwrap to fail; a handle that is not an
    // object answers null.
    DomElement? Dom.Features.IInsertAdjacentHost.FindElement(JsValue wrapper)
        => FindDomElementByJSObject(wrapper);

    void Dom.Features.IInsertAdjacentHost.InsertNodeAt(DomNode parent, DomNode node, int index) => InsertNodeAt(parent, node, index);
    DomText Dom.Features.IInsertAdjacentHost.CreateBridgeTextNode(string data) => CreateBridgeTextNode(data);
    List<DomNode> Dom.Features.IInsertAdjacentHost.BuildAdjacentHtmlNodes(DomElement contextElement, string html) => BuildAdjacentHtmlNodes(contextElement, html);
    void Dom.Features.IInsertAdjacentHost.ResetComputedStyleEngines() => ResetComputedStyleEngines();
}

// Explicit IElementContentHost implementation for the ElementContentBinding feature module: the
// innerHTML/outerHTML members route through the bridge's shared HTML parser/serializer and canonical tree
// mutation, so each forwards to the existing private serialize/set helpers. The textContent members are
// the canonical DomNode.TextContent, which the binding reaches without the bridge.
public sealed partial class DomBridge : Dom.Features.IElementContentHost
{
    IJsRealm Dom.Features.IElementContentHost.Realm => Realm;

    string Dom.Features.IElementContentHost.SerializeChildrenToHtml(DomElement element) => SerializeChildrenToHtml(element);
    string Dom.Features.IElementContentHost.SerializeElementToHtml(DomElement element) => SerializeElementToHtml(element);
    void Dom.Features.IElementContentHost.SetElementInnerHtml(DomElement element, string html) => SetElementInnerHtml(element, html);
    void Dom.Features.IElementContentHost.SetElementOuterHtml(DomElement element, string html) => SetElementOuterHtml(element, html);
}

// Explicit IElementReflectionHost implementation for the ElementReflectionBinding feature module:
// the bridge exposes only the current page URL (read at call time) so the URL-typed IDL
// getters resolve relative content attributes against the live document base; everything else the
// module needs is a neutral internal static bridge helper it calls directly.
public sealed partial class DomBridge : Dom.Features.IElementReflectionHost
{
    string Dom.Features.IElementReflectionHost.PageUrl => _pageUrl;
}

// Explicit IElementTraversalHost implementation for the ElementTraversalBinding feature module:
// the bridge exposes only its realm and the JS-wrapper factory via explicit interface members, so the
// module reaches no arbitrary bridge private field and the public surface is unchanged.
//
// The factory is a plain forward to WrapNode, which answers the handle the bridge's cache holds, so
// wrapper identity — and `el.firstElementChild === el.firstElementChild` with it — is preserved.
public sealed partial class DomBridge : Dom.Features.IElementTraversalHost
{
    IJsRealm Dom.Features.IElementTraversalHost.Realm => Realm;

    JsValue Dom.Features.IElementTraversalHost.ToWrapper(DomNode node) =>
        WrapNode(node);
}

// Explicit IGlobalAttributeHost implementation for the GlobalAttributeBinding feature module:
// the only bridge coupling the HTMLElement global attribute reflectors have is style-scope invalidation
// on a selector-affecting write (id/class/dir), forwarded here to the existing internal method.
public sealed partial class DomBridge : Dom.Features.IGlobalAttributeHost
{
    void Dom.Features.IGlobalAttributeHost.InvalidateStyleScope(DomElement element) => InvalidateStyleScope(element);
}

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="ITraversalHost"/>, the narrow contract the
/// extracted <see cref="Broiler.HtmlBridge.Dom.Features.TraversalBinding"/> feature module consumes.
/// Every member is an explicit interface
/// implementation, so none of these seams widen the public <c>DomBridge</c> surface — the module
/// reaches them only through the interface, never through bridge private fields.
/// </summary>
/// <remarks>
/// The traversal slice is not a half-migrated seam any more: the module speaks JSEAL and both wrapper
/// lookups below take the handle they are handed, so no cast is left in this file. Wrapper identity
/// (<c>el === el</c>, and the weak tables keyed on it) is the same question it was before, because
/// <c>Runtime/JsObjectRegistry</c> keys on <see cref="JsValue.ObjectIdentity"/> — the reference the
/// handle carries.
/// </remarks>
public sealed partial class DomBridge : ITraversalHost
{
    IJsRealm ITraversalHost.Realm => Realm;

    DomNode ITraversalHost.DocumentNode => _document;

    JsValue ITraversalHost.WrapNode(DomNode node) => WrapNode(node);

    DomNode? ITraversalHost.FindNode(JsValue wrapper) =>
        wrapper.IsObject ? FindDomNodeByJSObject(wrapper) : null;

    DomElement? ITraversalHost.FindElement(JsValue wrapper) =>
        wrapper.IsObject ? FindDomElementByJSObject(wrapper) : null;

    IReadOnlyList<(double Left, double Top, double Width, double Height)> ITraversalHost.GetClientRectsForRange(DomRange range) =>
        GetClientRectsForRange(range);

    JsValue ITraversalHost.CreateDomRect((double Left, double Top, double Width, double Height) rectData) =>
        CreateDomRectObject(rectData);

    JsValue ITraversalHost.CreateCommentNode(string data)
    {
        var comment = CreateBridgeCommentNode(data);
        return WrapNode(comment);
    }

    DomNode ITraversalHost.CreateRangeResultFragment() => CreateBridgeDocumentFragment();

    DomNode ITraversalHost.CloneRangeNode(DomNode node, bool deep)
    {
        var clone = CloneDomElement(node, deep);
        return clone;
    }

    DomText ITraversalHost.CreateRangeTextNode(string data)
    {
        var text = CreateBridgeTextNode(data);
        return text;
    }

    List<DomNode> ITraversalHost.ParseHtmlFragment(DomElement contextElement, string html) =>
        BuildAdjacentHtmlNodes(contextElement, html);

    DomElement ITraversalHost.CreateBridgeElement(string tagName) => CreateBridgeElement(tagName);

    bool ITraversalHost.HasBrowsingContext(DomNode documentRoot) =>
        documentRoot is DomDocument document &&
        _browsingContexts.GetContainerForDocument(document) is not null;
}

// Explicit ISelectorsHost implementation for the SelectorsBinding feature module: the bridge
// exposes selector validation, the descendant selector search, the two live element collections and the
// JS-wrapper factory via explicit interface members (the search and the two collection walks call static
// helpers that take the bridge; validation, matching and the wrapper factory forward to instance
// members), so the module reaches no arbitrary bridge private field and the public surface is unchanged.
//
// DomBridge/Utilities.cs and DomCollectionBinding both speak JSEAL, so the searches, the collection
// factory and the wrapper lookups below are the realm's and nothing here converts anything. The two
// `HTMLCollection` builders live here rather than in the module — a live collection's named getter
// runs on every property read, and the module is the same assembly, so moving them would buy
// nothing while both sides hold the same handles.
public sealed partial class DomBridge : Dom.Features.ISelectorsHost
{
    void Dom.Features.ISelectorsHost.ValidateSelector(string selector)
        => ValidateSelector(selector);

    JsValue Dom.Features.ISelectorsHost.FindInDescendants(DomElement element, string selector, bool all)
        => FromEngineResult(FindInDescendants(element, selector, all, this));

    JsValue Dom.Features.ISelectorsHost.ElementsByTagName(DomElement element, string tagName)
        => FromEngineResult(LiveCollection(() =>
        {
            var results = new List<JsValue>();
            CollectDescendantsByTag(element, tagName, results, this);
            return results;
        }));

    JsValue Dom.Features.ISelectorsHost.ElementsByClassName(DomElement element, string classNames)
        => FromEngineResult(LiveCollection(() =>
        {
            var results = new List<JsValue>();
            CollectDescendantsByClass(element, classNames, results, this);
            return results;
        }));

    JsValue Dom.Features.ISelectorsHost.ToWrapper(DomNode node) => WrapNode(node);

    bool Dom.Features.ISelectorsHost.MatchesSelector(DomElement element, string selector, DomElement? scope)
        => MatchesSelector(element, selector, scope);

    /// <summary>
    /// An <c>HTMLCollection</c> over <paramref name="contents"/>, with the named getter DOM
    /// §4.2.10.2 gives one: a lookup answers the first element whose <c>id</c> — or, for the
    /// elements HTML names, whose <c>name</c> — matches.
    /// </summary>
    private JsValue LiveCollection(Func<List<JsValue>> contents) =>
        Dom.Features.DomCollectionBinding.HtmlCollection(Realm, contents, name => NamedItem(Realm, contents, name));
}

// Explicit IShadowDomHost implementation for the ShadowDomBinding feature module: the
// per-element shadow linkage (host/root/mode) stays on the bridge's Shadow runtime slot and is exposed
// here as named primitives — the existing-root lookup, the mode read, and a single AttachShadowRoot that
// creates the #shadow-root element, parents it and records the mode in one step. Explicit interface
// members, so these seams do not widen the public DomBridge surface.
//
// The contract is spelled in JSEAL; IJsCalls.DomError raises the second-attachment
// NotSupportedError. The realm member must be explicit — DomBridge.Realm is internal, so an implicit implementation of a
// public interface member cannot see it (CS0737).
public sealed partial class DomBridge : Dom.Features.IShadowDomHost
{
    IJsRealm Dom.Features.IShadowDomHost.Realm => Realm;

    DomShadowRoot? Dom.Features.IShadowDomHost.GetShadowRoot(DomElement element) => element.ShadowRoot;

    DomShadowRoot Dom.Features.IShadowDomHost.AttachShadowRoot(
        DomElement host,
        DomShadowRootMode mode,
        bool delegatesFocus,
        DomSlotAssignmentMode slotAssignment)
    {
        _hasShadowRoots = true;
        return host.AttachShadow(mode, delegatesFocus, slotAssignment);
    }

    // The bridge's wrapper factory, forwarded as it stands: WrapNode answers the handle its wrapper
    // cache holds, so wrapper identity is unchanged.
    JsValue Dom.Features.IShadowDomHost.WrapNode(DomNode node) => WrapNode(node);
}

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="ICustomElementsHost"/> — the DOM services
/// the custom element registry needs, plus the realm it calls page code back through. Explicit
/// interface members, so calling into page code does not become part of the public <c>DomBridge</c>
/// surface.
/// </summary>
/// <remarks>
/// <para>
/// Constructing a definition, calling a reaction, the three promise factories and resolving a pending
/// promise are engine operations with no bridge state behind them, so the registry asks the realm for
/// them rather than asking the bridge to relay them. What is here is what only the bridge can answer.
/// </para>
/// <para>
/// <see cref="Realm"/> is implemented explicitly because it has to be: <c>DomBridge.Realm</c> is
/// <see langword="internal"/>, and an implicit implementation of a public interface member typed
/// against it does not compile (CS0737).
/// </para>
/// </remarks>
public sealed partial class DomBridge : ICustomElementsHost
{
    IJsRealm ICustomElementsHost.Realm => Realm;

    IReadOnlyList<DomElement> ICustomElementsHost.Elements => Elements;

    // The bridge's wrapper factory, forwarded as it stands: WrapNode answers the handle
    // JsObjectRegistry caches for the node, so wrapper identity is unchanged.
    JsValue ICustomElementsHost.WrapNode(DomNode node) => WrapNode(node);

    bool ICustomElementsHost.TryGetWrapper(DomElement element, out JsValue wrapper)
    {
        if (_jsObjects.TryGet(element, out var cached))
        {
            wrapper = cached;
            return true;
        }

        wrapper = JsValue.Missing;
        return false;
    }

    // A plain forward: the reverse lookup takes the same handle. The registry guards with IsObject
    // before it calls, and a handle that is not an object would answer null rather than throw.
    DomNode? ICustomElementsHost.FindNode(JsValue wrapper) =>
        FindDomNodeByJSObject(wrapper);

    DomElement ICustomElementsHost.CreateBridgeElement(string tagName) => CreateBridgeElement(tagName);

    /// <summary>
    /// Whether the element is in a document tree. The connected test walks to the root and asks
    /// whether it is a document, rather than testing for a parent: a subtree assembled off-tree
    /// has parents all the way up and is still not connected, and <c>connectedCallback</c> must not
    /// run for it.
    /// </summary>
    /// <remarks>
    /// Any document, not only the page's. A node adopted into a frame's or a
    /// <c>createHTMLDocument</c>'s tree is connected there, and a browser runs its
    /// <c>connectedCallback</c> — measured, the cross-document <c>appendChild</c> shape reports
    /// connected, disconnected, adopted, connected.
    /// </remarks>
    bool ICustomElementsHost.IsConnected(DomElement element) => element.IsConnected;

    DomElement? ICustomElementsHost.FormOwnerOf(DomElement element) =>
        Dom.Features.FormAssociationBinding.FormOwnerOf(this, element);

    bool ICustomElementsHost.IsFormControlDisabled(DomElement element) => IsFormControlDisabled(element);
}

/// <summary>
/// <see cref="DomBridge"/>'s implementation of <see cref="IElementInternalsHost"/> — the
/// custom-element and form-association reads a form-associated custom element's
/// <c>ElementInternals</c> answers with. Explicit interface members, so the seam does not widen the
/// public <c>DomBridge</c> surface.
/// </summary>
/// <remarks>
/// <para>
/// The contract is spelled in JSEAL, and no member here converts: each forwards the realm, a DOM
/// read or a handle the bridge or a binding answered, and the one event it fires is a realm object
/// handed to the dispatcher as it is.
/// </para>
/// <para>
/// <see cref="Realm"/> is implemented explicitly because it has to be: <c>DomBridge.Realm</c> is
/// <see langword="internal"/>, and an implicit implementation of an interface member typed against it
/// does not compile (CS0737).
/// </para>
/// </remarks>
public sealed partial class DomBridge : IElementInternalsHost
{
    private ElementInternalsBinding? _elementInternals;

    internal ElementInternalsBinding ElementInternals =>
        _elementInternals ??= new ElementInternalsBinding(this);

    IJsRealm IElementInternalsHost.Realm => Realm;

    JsValue IElementInternalsHost.WrapNode(DomNode node) => WrapNode(node);

    bool IElementInternalsHost.IsCustomElement(DomElement element) => CustomElements.IsCustom(element);

    bool IElementInternalsHost.IsFormAssociatedCustomElement(DomElement element) =>
        CustomElements.IsFormAssociated(element);

    DomElement? IElementInternalsHost.FormOwnerOf(DomElement element) =>
        FormAssociationBinding.FormOwnerOf(this, element);

    bool IElementInternalsHost.IsDisabled(DomElement element) => IsFormControlDisabled(element);

    JsValue IElementInternalsHost.LabelsFor(DomElement element) =>
        FormAssociationBinding.LabelsList(this, element);

    /// <summary>
    /// The element's shadow root. An internals reports the same root <c>element.shadowRoot</c> does —
    /// it is the element's own — so this goes through the one implementation rather than a second.
    /// </summary>
    JsValue IElementInternalsHost.ShadowRootOf(DomElement element) =>
        ShadowDomBinding.GetShadowRoot(this, element);

    /// <summary>
    /// Fires a non-bubbling cancelable <c>invalid</c> event at the element — what
    /// <c>checkValidity</c> does when it is about to answer <see langword="false"/>, and how a page
    /// hears about a failed control without polling every one of them.
    /// </summary>
    /// <remarks>
    /// The event object is built through the realm; its three members keep the
    /// enumerable/configurable data-property attributes they had, which is what
    /// <see cref="JsPropertyFlags.Default"/> spells. The dispatcher takes that handle as it is.
    /// </remarks>
    void IElementInternalsHost.DispatchInvalidEvent(DomElement element)
    {
        var evt = Realm.NewObject();
        Realm.DefineValue(evt, "type", JsValue.String("invalid"));
        Realm.DefineValue(evt, "bubbles", JsValue.False);
        Realm.DefineValue(evt, "cancelable", JsValue.True);
        _eventDispatch.DispatchEventOnElement(element, evt);
    }
}
