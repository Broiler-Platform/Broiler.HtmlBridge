using System.Runtime.CompilerServices;
using Broiler.Dom;
using Broiler.JSeal;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// <c>Element</c> as a real interface: its members on <c>Element.prototype</c>, found through the
/// receiver, rather than copied onto every element wrapper in the document.
/// </summary>
/// <remarks>
/// <para>
/// This is the element half of track 6's wrapper item, and it follows the character-data move
/// (<c>DomBridge/NodeInterfaces.cs</c>, which describes the receiver mechanism) and the
/// <c>Node</c>-member deletion that came after it. An element carried <b>140</b> own properties where
/// a browser gives it none, and <c>Element.prototype.getAttribute</c> was
/// <see langword="undefined"/> — so the ordinary defensive idiom
/// <c>Element.prototype.matches.call(el, sel)</c> threw, and a page extending
/// <c>Element.prototype</c> assigned to an object every element ignored.
/// </para>
/// <para>
/// <b>What moves is exactly Web IDL's <c>Element</c>,</b> plus the mixins the <c>Element</c> interface
/// includes — <c>ParentNode</c>, <c>ChildNode</c>, <c>NonDocumentTypeChildNode</c>, the CSSOM View
/// box metrics, <c>Fullscreen</c> and <c>Animatable</c>. Nothing else, so the prototype ends with the
/// shape a browser's has rather than with whatever the wrapper happened to carry:
/// <c>title</c>/<c>lang</c>/<c>dir</c>/<c>draggable</c>/<c>accessKey</c>, <c>style</c>,
/// <c>dataset</c>, <c>innerText</c>, <c>click</c>/<c>focus</c>/<c>blur</c>, the <c>on*</c> handlers
/// and the <c>offset*</c> metrics are <c>HTMLElement</c>'s, installed on its prototype by
/// the <c>HTMLElement</c> partial below (a non-HTML element, or a wrapper minted before that
/// prototype is ready, carries its own copies); <c>appendChild</c> and the other four tree mutations
/// are <c>Node</c>'s;
/// <c>textContent</c> is <c>Node</c>'s and still the element's own (it was installed because an
/// element's operation differed from a character-data node's, and both are the canonical
/// <c>DomNode.TextContent</c> now); and <c>data</c>, <c>length</c>, <c>scrollParent</c> and
/// <c>removeAttributeNodeNS</c> are on no browser's <c>Element.prototype</c> at all, so they are not
/// smuggled onto this one.
/// </para>
/// <para>
/// <b>One installer serves both places.</b> Each member is written once, against a
/// <see cref="Dom.Features.JsElementSource"/> that answers either the element captured when the
/// wrapper was built or the element the receiver names. The prototype gets the receiver-resolving
/// source and a wrapper minted before the realm exists — which inherits from nothing — gets the
/// capturing one. The two cannot drift, which is what the earlier moves had to establish by reading
/// every copy against its prototype counterpart by hand.
/// </para>
/// <para>
/// <b>The installer speaks JSEAL, all of it.</b> Every member is minted through <see cref="Realm"/>
/// and installed on the target handle, in the position it is written in, so
/// <c>Object.getOwnPropertyNames</c> reports the order it always did. That includes <c>animate</c>:
/// its body, <c>DomBridge/Animations.cs</c>'s <see cref="ElementAnimate"/>, takes a
/// <see cref="JsCall"/> and reads the keyframes and the options object through the realm, and it is
/// installed with <see cref="AddInterfaceMethod"/>, minted through the realm like every other member.
/// The four <c>ChildNode</c> members and the fullscreen pair are realm-minted the same way.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>
    /// Whether <c>Element.prototype</c> carries the interface yet, which is what lets an element
    /// wrapper stop installing its own copy of it.
    /// </summary>
    private bool _elementInterfacePrototypeReady;

    /// <summary>
    /// One <c>DOMTokenList</c> per element, so <c>el.classList === el.classList</c> holds now that
    /// the member is a prototype accessor rather than a value built with the wrapper.
    /// </summary>
    /// <remarks>
    /// A weak table, like the <c>NamedNodeMap</c> cache <c>attributes</c> already uses: the list reads
    /// and writes the element's <c>class</c> attribute on every call, so a second instance would be
    /// redundant rather than fresher, and neither cache should keep an element alive after the page
    /// has dropped it. The box is because a <see cref="ConditionalWeakTable{TKey,TValue}"/> value must
    /// be a reference type and a <see cref="JsValue"/> handle is a struct.
    /// </remarks>
    private readonly ConditionalWeakTable<DomElement, StrongBox<JsValue>> _classLists = new();

    /// <summary>
    /// Installs <c>Element</c>'s members on <c>Element.prototype</c>. A no-op when the realm does not
    /// carry the interface.
    /// </summary>
    internal void RegisterElementInterface()
    {
        var proto = PrototypeHandleOfInterface("Element");
        if (!proto.IsObject)
            return;

        InstallElementInterface(proto, RequireElementReceiver, RequireWrapperReceiver);
        _elementInterfacePrototypeReady = true;
    }

    /// <summary>
    /// <c>Element</c>'s members as own properties of one wrapper — the shape every element had before
    /// they moved, kept for the one case that cannot use the prototype: a wrapper minted before the
    /// realm carried the interfaces, which inherits from nothing.
    /// </summary>
    /// <remarks>
    /// Both sources capture rather than resolve: the element is the one the wrapper was minted for and
    /// the wrapper is this object, whatever receiver a call happens to arrive with. That is what an own
    /// property of one wrapper means, and it is why neither raises the illegal-invocation
    /// <c>TypeError</c> the prototype's pair does.
    /// </remarks>
    private void PopulateElementInterfaceOnInstance(JsValue wrapper, DomElement element) =>
        InstallElementInterface(wrapper, (in JsCall _, string _) => element, (in JsCall _, string _) => wrapper);

    /// <summary>The element the receiver names, or a <c>TypeError</c> when it is not one.</summary>
    /// <remarks>
    /// A browser answers the same for a receiver of the wrong kind — <c>Element.prototype.getAttribute
    /// .call(document, 'x')</c> and <c>.call(textNode, 'x')</c> are both illegal invocations, because
    /// neither implements <c>Element</c> however node-like it is.
    /// </remarks>
    private DomElement RequireElementReceiver(in JsCall call, string member)
    {
        // The wrapper registry keys on JsValue.ObjectIdentity, so the receiver is looked up as it
        // stands. A non-object receiver answers the TypeError without a lookup.
        if (call.This.IsObject &&
            _jsObjects.TryGetNode(call.This, out var node) &&
            node is DomElement element)
        {
            return element;
        }

        throw call.Realm.Error(JsErrorKind.TypeError,
            $"Failed to execute '{member}' on 'Element': Illegal invocation");
    }

    /// <summary>The receiver itself, once it is known to be an element wrapper.</summary>
    private JsValue RequireWrapperReceiver(in JsCall call, string member)
    {
        if (call.This.IsObject &&
            _jsObjects.TryGetNode(call.This, out var node) &&
            node is DomElement)
        {
            return call.This;
        }

        throw call.Realm.Error(JsErrorKind.TypeError,
            $"Failed to execute '{member}' on 'Element': Illegal invocation");
    }

    /// <summary>Adds a WebIDL operation to an interface prototype.</summary>
    /// <remarks>
    /// Enumerable and configurable but not writable-as-data is what the instance properties were, and
    /// what Web IDL asks for on a prototype; keeping the same attributes means only the *location* of
    /// the member changes. <see cref="JsPropertyFlags.Default"/> is that pair, which is why it is not
    /// spelled at any of these call sites.
    /// </remarks>
    private void AddInterfaceMethod(JsValue target, string name, int length, JsNativeFunction body) =>
        Realm.DefineValue(target, name, Realm.NewMethod(name, body, length));

    /// <summary>Adds a WebIDL attribute to an interface prototype, read-only unless a setter is given.</summary>
    private void AddInterfaceAccessor(JsValue target, string name,
        JsNativeFunction getter, JsNativeFunction? setter = null) =>
        Realm.DefineAccessor(target, name, getter, setter);

    /// <summary>
    /// The whole <c>Element</c> interface onto <paramref name="target"/> — <c>Element.prototype</c>,
    /// or one wrapper when there is no prototype to inherit from.
    /// </summary>
    private void InstallElementInterface(JsValue target, Dom.Features.JsElementSource element, Dom.Features.WrapperSource wrapper)
    {
        InstallElementIdentityMembers(target, element);
        InstallElementAttributeMembers(target, element, wrapper);
        InstallElementContentMembers(target, element);
        InstallElementTreeMembers(target, element);
        InstallElementSelectionMembers(target, element);

        Dom.Features.ElementGeometryBinding.InstallElementMembers(this, Realm, target, element);

        // Fullscreen's requestFullscreen()/webkitRequestFullscreen(), which the dialog/details/popover
        // module owns because they share its top-layer machinery. The realm's, in this position.
        _dialogs.InstallElementMembers(target, element);

        // Animatable.animate() — Web Animations §Animatable, which Element includes. Minted at 2,
        // which is this bridge's count rather than Web IDL's 1; correcting that is a separate
        // decision from moving a frame, and DomEnumerationAndArityTests pins the 2 so it cannot move
        // by accident.
        AddInterfaceMethod(target, "animate", 2,
            (in call) => ElementAnimate(element(in call, "animate"), in call));
    }

    /// <summary>
    /// <c>tagName</c>, the reflected <c>id</c>/<c>className</c>, <c>classList</c> and the shadow-host
    /// pair.
    /// </summary>
    private void InstallElementIdentityMembers(JsValue target, Dom.Features.JsElementSource element)
    {
        // tagName is an accessor here where the wrapper installed a JSString fixed when it was built.
        // That was the "per-instance value" half of the item: a captured value cannot serve a
        // prototype, and a browser's tagName is an accessor in any case.
        AddInterfaceAccessor(target, "tagName",
            (in call) => JsValue.String(TagNameForScript(element(in call, "tagName"))));

        Dom.Features.GlobalAttributeBinding.InstallElementMembers(this, Realm, target, element);

        // classList — one DOMTokenList per element, memoized so identity holds (see _classLists).
        AddInterfaceAccessor(target, "classList",
            (in call) => ClassListFor(element(in call, "classList")));

        AddInterfaceAccessor(target, "shadowRoot",
            (in call) => Dom.Features.ShadowDomBinding.GetShadowRoot(this, element(in call, "shadowRoot")));
        AddInterfaceMethod(target, "attachShadow", 1,
            (in call) => Dom.Features.ShadowDomBinding.AttachShadow(
                this,
                element(in call, "attachShadow"),
                // Only an object argument carries options — the test the engine frame applied, kept
                // here so anything else still leaves the mode at its default.
                call.Length > 0 && call[0].IsObject ? call[0] : JsValue.Undefined));
    }

    /// <summary>
    /// The attribute surface (DOM §4.9): the live <c>NamedNodeMap</c>, the name and namespace
    /// accessors, and the <c>Attr</c>-node operations.
    /// </summary>
    /// <remarks>
    /// <c>removeAttributeNodeNS</c> is deliberately absent. The wrapper installs one, and no browser
    /// does: DOM §4.9 pairs <c>setAttributeNode</c> with <c>setAttributeNodeNS</c> but gives
    /// <c>removeAttributeNode</c> no namespace-qualified sibling, since an <c>Attr</c> already knows
    /// its own namespace. Putting it here would give <c>Element.prototype</c> a member a browser's has
    /// not got, so it stays the instance's until it is decided on its own.
    /// </remarks>
    private void InstallElementAttributeMembers(JsValue target, Dom.Features.JsElementSource element, Dom.Features.WrapperSource wrapper)
    {
        AddInterfaceAccessor(target, "attributes", (in call) =>
            _attributes.BuildNamedNodeMap(element(in call, "attributes"), wrapper(in call, "attributes")));

        AddInterfaceMethod(target, "getAttribute", 1,
            (in call) => _attributes.GetAttribute(element(in call, "getAttribute"), in call));
        AddInterfaceMethod(target, "getAttributeNS", 2,
            (in call) => _attributes.GetAttributeNS(element(in call, "getAttributeNS"), in call));
        AddInterfaceMethod(target, "getAttributeNames", 0, (in call) =>
            Realm.NewArray([.. AttributeNames(element(in call, "getAttributeNames")).Select(static name => JsValue.String(name))]));

        AddInterfaceMethod(target, "setAttribute", 2,
            (in call) => _attributes.SetAttribute(element(in call, "setAttribute"), in call));
        AddInterfaceMethod(target, "setAttributeNS", 3,
            (in call) => _attributes.SetAttributeNS(element(in call, "setAttributeNS"), in call));

        AddInterfaceMethod(target, "removeAttribute", 1,
            (in call) => _attributes.RemoveAttribute(element(in call, "removeAttribute"), in call));
        AddInterfaceMethod(target, "removeAttributeNS", 2,
            (in call) => _attributes.RemoveAttributeNS(element(in call, "removeAttributeNS"), in call));
        AddInterfaceMethod(target, "toggleAttribute", 2,
            (in call) => _attributes.ToggleAttribute(element(in call, "toggleAttribute"), in call));

        AddInterfaceMethod(target, "hasAttribute", 1,
            (in call) => _attributes.HasAttribute(element(in call, "hasAttribute"), in call));
        AddInterfaceMethod(target, "hasAttributeNS", 2,
            (in call) => _attributes.HasAttributeNS(element(in call, "hasAttributeNS"), in call));
        AddInterfaceMethod(target, "hasAttributes", 0, (in call) =>
            JsValue.Boolean(element(in call, "hasAttributes").Attributes.Count > 0));

        AddInterfaceMethod(target, "getAttributeNode", 1, (in call) =>
            _attributes.GetAttributeNode(element(in call, "getAttributeNode"), wrapper(in call, "getAttributeNode"), in call));
        AddInterfaceMethod(target, "getAttributeNodeNS", 2, (in call) =>
            _attributes.GetAttributeNodeNS(element(in call, "getAttributeNodeNS"), wrapper(in call, "getAttributeNodeNS"), in call));
        AddInterfaceMethod(target, "setAttributeNode", 1, (in call) =>
            _attributes.SetAttributeNode(element(in call, "setAttributeNode"), wrapper(in call, "setAttributeNode"), in call));
        AddInterfaceMethod(target, "setAttributeNodeNS", 1, (in call) =>
            _attributes.SetAttributeNodeNS(element(in call, "setAttributeNodeNS"), wrapper(in call, "setAttributeNodeNS"), in call));
        AddInterfaceMethod(target, "removeAttributeNode", 1, (in call) =>
            _attributes.RemoveAttributeNode(element(in call, "removeAttributeNode"), wrapper(in call, "removeAttributeNode"), in call));
    }

    /// <summary>The markup members: <c>innerHTML</c>/<c>outerHTML</c> and the three adjacent inserts.</summary>
    private void InstallElementContentMembers(JsValue target, Dom.Features.JsElementSource element)
    {
        Dom.Features.ElementContentBinding.InstallHtmlSerialization(this, Realm, target, element);
        Dom.Features.InsertAdjacentBinding.Install(this, Realm, target, element);
    }

    /// <summary>
    /// The tree members <c>Element</c> carries: the <c>ParentNode</c> element views and inserts, the
    /// <c>ChildNode</c> mixin, and the two <c>NonDocumentTypeChildNode</c> siblings.
    /// </summary>
    private void InstallElementTreeMembers(JsValue target, Dom.Features.JsElementSource element)
    {
        AddInterfaceAccessor(target, "children", (in call) =>
            Dom.Features.ElementTraversalBinding.GetChildren(this, element(in call, "children")));
        AddInterfaceAccessor(target, "childElementCount", (in call) =>
            JsValue.Number(element(in call, "childElementCount").ChildElementCount));
        AddInterfaceAccessor(target, "firstElementChild", (in call) =>
            Dom.Features.ElementTraversalBinding.GetFirstElementChild(this, element(in call, "firstElementChild")));
        AddInterfaceAccessor(target, "lastElementChild", (in call) =>
            Dom.Features.ElementTraversalBinding.GetLastElementChild(this, element(in call, "lastElementChild")));
        AddInterfaceAccessor(target, "nextElementSibling", (in call) =>
            Dom.Features.ElementTraversalBinding.GetNextElementSibling(this, element(in call, "nextElementSibling")));
        AddInterfaceAccessor(target, "previousElementSibling", (in call) =>
            Dom.Features.ElementTraversalBinding.GetPreviousElementSibling(this, element(in call, "previousElementSibling")));

        AddInterfaceMethod(target, "append", 0,
            (in call) => Dom.Features.TreeMutationBinding.Append(this, element(in call, "append"), in call));
        AddInterfaceMethod(target, "prepend", 0,
            (in call) => Dom.Features.TreeMutationBinding.Prepend(this, element(in call, "prepend"), in call));
        // replaceChildren is the ParentNode member the wrapper never had: the document's has been
        // here since the mixin was bound there, and an element's — the commoner one, since
        // `container.replaceChildren()` is how a page empties a node — threw as undefined.
        AddInterfaceMethod(target, "replaceChildren", 0,
            (in call) => Dom.Features.TreeMutationBinding.ReplaceChildren(this, element(in call, "replaceChildren"), in call));

        // The ChildNode mixin. These four bodies were the last engine-framed group in this file, and
        // not because of this file: DomBridge/NodeInterfaces.cs and
        // DomBridge/JsObjects.NonElementNodes.cs install the same four members on
        // CharacterData.prototype and on the non-element wrappers, so ChildNodeBinding had to serve
        // three callers at once and kept a second, engine-framed entry point per operation to do it.
        // All three mint through the realm now, so there is one entry point again and these are
        // ordinary interface methods.
        AddInterfaceMethod(target, "remove", 0,
            (in call) => Dom.Features.ChildNodeBinding.Remove(this, element(in call, "remove"), in call));
        AddInterfaceMethod(target, "before", 0,
            (in call) => Dom.Features.ChildNodeBinding.Before(this, element(in call, "before"), in call));
        AddInterfaceMethod(target, "after", 0,
            (in call) => Dom.Features.ChildNodeBinding.After(this, element(in call, "after"), in call));
        AddInterfaceMethod(target, "replaceWith", 0,
            (in call) => Dom.Features.ChildNodeBinding.ReplaceWith(this, element(in call, "replaceWith"), in call));
    }

    /// <summary>The selector and collection lookups scoped to an element.</summary>
    private void InstallElementSelectionMembers(JsValue target, Dom.Features.JsElementSource element)
    {
        AddInterfaceMethod(target, "querySelector", 1, (in call) =>
            Dom.Features.SelectorsBinding.QuerySelector(this, element(in call, "querySelector"), StringArgument(in call)));
        AddInterfaceMethod(target, "querySelectorAll", 1, (in call) =>
            Dom.Features.SelectorsBinding.QuerySelectorAll(this, element(in call, "querySelectorAll"), StringArgument(in call)));
        AddInterfaceMethod(target, "matches", 1, (in call) =>
            Dom.Features.SelectorsBinding.Matches(this, element(in call, "matches"), StringArgument(in call)));
        AddInterfaceMethod(target, "closest", 1, (in call) =>
            Dom.Features.SelectorsBinding.Closest(this, element(in call, "closest"), StringArgument(in call)));
        AddInterfaceMethod(target, "getElementsByTagName", 1, (in call) =>
            Dom.Features.SelectorsBinding.GetElementsByTagName(this, element(in call, "getElementsByTagName"), StringArgument(in call)));
        AddInterfaceMethod(target, "getElementsByClassName", 1, (in call) =>
            Dom.Features.SelectorsBinding.GetElementsByClassName(this, element(in call, "getElementsByClassName"), StringArgument(in call)));
    }

    /// <summary>The element's one <c>DOMTokenList</c>, built on first use.</summary>
    private JsValue ClassListFor(DomElement element) =>
        _classLists.GetValue(element, key => new StrongBox<JsValue>(
            Dom.Features.ClassListBinding.Build(Realm, key, InvalidateStyleScope))).Value;
}

/// <summary>
/// <c>HTMLElement</c> as a real interface: the members HTML gives every HTML element — and the mixins
/// it includes — on <c>HTMLElement.prototype</c> rather than copied onto every element wrapper.
/// </summary>
/// <remarks>
/// <para>
/// The sixth instalment of track 6's wrapper item, and the direct sequel to
/// the <c>Element</c> partial above, whose <see cref="Dom.Features.JsElementSource"/> mechanism it
/// reuses unchanged: each member is written once and installed either on the prototype, where it
/// resolves its element from the receiver, or on one wrapper, where it closes over the element it was
/// built for. <c>HTMLElement.prototype</c> owned nothing but its <c>constructor</c>; it owned 37
/// members once this landed, and an element went from 77 own properties to 40.
/// </para>
/// <para>
/// <b>What moves is Web IDL's <c>HTMLElement</c>, plus the mixins it includes</b> —
/// <c>ElementCSSInlineStyle</c> (<c>style</c>), <c>HTMLOrSVGElement</c> (<c>dataset</c>,
/// <c>tabIndex</c>, <c>focus</c>, <c>blur</c>), <c>GlobalEventHandlers</c> (the seventeen <c>on*</c>
/// reflectors) and the CSSOM View <c>offset*</c> metrics. Not the per-control reflectors beside them:
/// <c>value</c>, <c>checked</c>, <c>type</c>, <c>name</c>, <c>disabled</c>, <c>required</c> and
/// <c>files</c> are installed on every element here where a browser gives them only to the interfaces
/// that declare them, so relocating them is a decision about dropping them from a <c>&lt;div&gt;</c>
/// rather than a relocation. <c>textContent</c> is <c>Node</c>'s, not <c>HTMLElement</c>'s, and
/// stays each wrapper's own (see <c>ElementContentBinding.InstallTextContent</c>).
/// </para>
/// <para>
/// <b>An SVG element keeps its own copies.</b> It does not inherit <c>HTMLElement.prototype</c> —
/// <c>SVGElement</c> derives straight from <c>Element</c> — so it installs the same members on itself,
/// exactly as it did before. That is deliberately today's behaviour and not a browser's: an
/// <c>SVGElement</c> shares only three of these mixins and has no <c>title</c>, <c>innerText</c> or
/// <c>offsetWidth</c>. Narrowing it is the per-tag SVG interface decision this track already holds
/// open, and doing it here would mean minting a prototype shape from a specification reading rather
/// than from a measurement.
/// </para>
/// <para>
/// <b>Two members are per-instance objects</b> and needed the treatment <c>classList</c> got:
/// <c>style</c> was a declaration built with the wrapper and captured by the accessor, and
/// <c>dataset</c> a self-replacing accessor that wrote its map back onto the wrapper it closed over.
/// Both are weak per-element caches now, so <c>el.style === el.style</c> and
/// <c>el.dataset === el.dataset</c> hold while the element itself carries neither.
/// </para>
/// <para>
/// <b>The installer speaks JSEAL, all of it.</b> Every member is minted through <see cref="Realm"/>
/// onto the handle the installer is given, and nothing converts it on the way, so every member lands
/// in the order it is written in.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>
    /// Whether <c>HTMLElement.prototype</c> carries the interface yet, which is what lets an HTML
    /// element's wrapper stop installing its own copy of it.
    /// </summary>
    private bool _htmlElementInterfacePrototypeReady;

    /// <summary>One inline <c>CSSStyleDeclaration</c> per element, so <c>el.style === el.style</c>.</summary>
    /// <remarks>
    /// The declaration is a live object — it writes each mutation through to the <c>style</c> content
    /// attribute and invalidates the style scope — so a second instance would be redundant rather than
    /// fresher, and rebuilding one per read would drop whatever the page had set on it. The box is
    /// because a <see cref="ConditionalWeakTable{TKey,TValue}"/> value must be a reference type and a
    /// <see cref="JsValue"/> handle is a struct.
    /// </remarks>
    private readonly ConditionalWeakTable<DomElement, StrongBox<JsValue>> _inlineStyles = new();

    /// <summary>One <c>DOMStringMap</c> per element, on the same terms.</summary>
    /// <remarks>
    /// Built on first read rather than with the element: a document has thousands of elements and few
    /// are ever asked for their dataset, so building every map up front would allocate a proxy and
    /// four callbacks per element for nothing. That was the reason the instance version was a
    /// self-replacing accessor; a weak table gives the same laziness without leaving an own property
    /// behind.
    /// </remarks>
    private readonly ConditionalWeakTable<DomElement, StrongBox<JsValue>> _datasets = new();

    /// <summary>
    /// Installs <c>HTMLElement</c>'s members on <c>HTMLElement.prototype</c>. A no-op when the realm
    /// does not carry the interface.
    /// </summary>
    internal void RegisterHtmlElementInterface()
    {
        var proto = PrototypeHandleOfInterface("HTMLElement");
        if (!proto.IsObject)
            return;

        InstallHtmlElementInterface(proto, RequireElementReceiver);
        _htmlElementInterfacePrototypeReady = true;
    }

    /// <summary>
    /// <c>HTMLElement</c>'s members as own properties of one wrapper — for an SVG element, which does
    /// not inherit the interface, and for a wrapper minted before the realm carried it.
    /// </summary>
    /// <remarks>
    /// The source captures rather than resolves: the element is the one this wrapper was minted for,
    /// whatever receiver a call happens to arrive with. That is what an own property of one wrapper
    /// means, and it is why it never raises the illegal-invocation <c>TypeError</c> the prototype's
    /// source does.
    /// </remarks>
    private void PopulateHtmlElementInterfaceOnInstance(JsValue wrapper, DomElement element) =>
        InstallHtmlElementInterface(wrapper, (in JsCall _, string _) => element);

    /// <summary>
    /// The whole <c>HTMLElement</c> interface onto <paramref name="target"/> —
    /// <c>HTMLElement.prototype</c>, or one wrapper that cannot inherit from it.
    /// </summary>
    private void InstallHtmlElementInterface(JsValue target, Dom.Features.JsElementSource element)
    {
        Dom.Features.GlobalAttributeBinding.InstallHtmlElementMembers(this, Realm, target, element);
        Dom.Features.ElementContentBinding.InstallHtmlElementMembers(Realm, target, element);

        // hidden and tabIndex — the two genuinely global reflectors the form-control module carries.
        // The realm's, in this position, since that module reads a JsCall now.
        _formControl.InstallHtmlElementMembers(target, element);

        // style — ElementCSSInlineStyle. Assigning a string sets cssText rather than replacing the
        // object, which is why the setter is here and not a plain data property.
        Realm.DefineAccessor(target, "style",
            (in call) => InlineStyleFor(element(in call, "style")),
            (in call) =>
            {
                // The receiver is resolved before the value is looked at, as it was when the engine
                // frame carried the string test inside the callee: assigning a non-string to the
                // setter with a receiver that is not an element still raises the TypeError.
                var styled = element(in call, "style");

                // Only a string right-hand side acts — a quirk preserved verbatim from the bridge's
                // original element.style setter, and the reason StyleDeclarationBinding keeps its
                // string-typed overload beside the cssText one that stringifies anything.
                if (call.Length > 0 && call[0].IsString)
                {
                    Dom.Features.StyleDeclarationBinding.SetInlineStyleCssText(
                        this, styled, InlineStyleMutation(styled), call[0].AsString!);
                }

                return JsValue.Undefined;
            });

        // dataset — HTMLOrSVGElement's live DOMStringMap over the data-* attributes.
        Realm.DefineAccessor(target, "dataset",
            (in call) => DatasetFor(element(in call, "dataset")), null);

        // click/focus/blur are EventTargetBinding's, and they are installed the way attachInternals
        // is below -- same object, same position, same element source.
        AddInterfaceMethod(target, "click", 0,
            (in call) => Dom.Features.EventTargetBinding.Click(this, element(in call, "click"), in call));
        AddInterfaceMethod(target, "focus", 0,
            (in call) => Dom.Features.EventTargetBinding.Focus(this, element(in call, "focus"), in call));
        AddInterfaceMethod(target, "blur", 0,
            (in call) => Dom.Features.EventTargetBinding.Blur(this, element(in call, "blur"), in call));

        // attachInternals() — HTML §4.13.5, a member of HTMLElement rather than of the custom
        // elements only, which is what makes the standard feature-detect answer the right way. It
        // refuses at call time for an element that is not a form-associated custom element.
        AddInterfaceMethod(target, "attachInternals", 0,
            (in call) => ElementInternals.AttachInternals(element(in call, "attachInternals")));

        InstallInlineEventHandlerMembers(target, element);

        Dom.Features.ElementGeometryBinding.InstallHtmlElementMembers(this, Realm, target, element);
    }

    /// <summary>
    /// The <c>GlobalEventHandlers</c> reflectors — <c>onclick</c>, <c>onload</c> and the rest of
    /// <see cref="DomBridgeUtils.InlineEventNames"/>.
    /// </summary>
    private void InstallInlineEventHandlerMembers(JsValue target, Dom.Features.JsElementSource element)
    {
        foreach (var name in InlineEventNames)
        {
            // Captured per iteration: the loop variable is one shared binding by the time a handler
            // runs, so reading it inside the closure would give every reflector the last name.
            var eventName = name;
            var member = "on" + eventName;

            // EventHandlerReflectorBinding is migrated, so the realm mints and names the pair itself.
            // Minting them through the realm rather than wrapping a bridge function object around it is
            // what gives the setter a properly tagged argument, and so lets "is it a function" — the
            // whole of what the setter decides — stay the binding's question.
            Realm.DefineAccessor(target, member,
                (in call) => Dom.Features.EventHandlerReflectorBinding.GetOn(
                    this, element(in call, member), eventName, in call),
                (in call) => Dom.Features.EventHandlerReflectorBinding.SetOn(
                    this, element(in call, member), eventName, in call));
        }
    }

    /// <summary>The element's one inline style declaration, built on first use.</summary>
    private JsValue InlineStyleFor(DomElement element) =>
        _inlineStyles.GetValue(element, key => new StrongBox<JsValue>(
            Dom.Features.StyleDeclarationBinding.BuildInlineDeclaration(
                Realm, this, key, InlineStyleMutation(key),
                onPositionAreaInvalidate: ClearPositionAreaResolution))).Value;

    /// <summary>
    /// What every inline-style mutation owes: write the dict through to the canonical <c>style</c>
    /// attribute, so <c>el.style</c> and <c>getAttribute("style")</c> observe one state, then
    /// invalidate the computed style.
    /// </summary>
    /// <remarks>
    /// Both the declaration object's own mutations (per property, <c>cssText</c>, <c>setProperty</c>,
    /// <c>removeProperty</c>, <c>cssFloat</c>) and the <c>el.style = "…"</c> assignment run it.
    /// </remarks>
    private Action InlineStyleMutation(DomElement element) => () =>
    {
        SyncStyleAttributeFromInlineStyle(element);
        InvalidateStyleScope(element);
    };

    /// <summary>
    /// The element's one <c>DOMStringMap</c>, built on first use — or <see langword="undefined"/> when
    /// the realm has no <c>Proxy</c> to build it from, which is honest: an absent dataset is at least
    /// not one that silently drops writes.
    /// </summary>
    private JsValue DatasetFor(DomElement element)
    {
        if (_datasets.TryGetValue(element, out var cached))
            return cached.Value;

        // The guard asks the realm now, which is what it always meant: the realm and the context
        // are adopted together and cleared together, so this is the same question in the vocabulary
        // that survives. A realm with no Proxy still answers undefined rather than a map that would
        // silently drop writes.
        if (_realm is null ||
            Dom.Features.DatasetBinding.Build(Realm, element, InvalidateStyleScope) is not { IsObject: true } dataset)
        {
            return JsValue.Undefined;
        }

        _datasets.Add(element, new StrongBox<JsValue>(dataset));
        return dataset;
    }
}

/// <summary>
/// Links a DOM wrapper to its interface's prototype, so <c>constructor.name</c> and
/// <c>Object.getPrototypeOf</c> answer the interface rather than <c>Object</c>.
/// </summary>
/// <remarks>
/// <para>
/// Every wrapper reported <c>constructor.name</c> of <c>"Object"</c> — a text node, a comment, a
/// fragment, an attribute, an element, all of them. <c>instanceof</c> already answered correctly,
/// because the interface globals carry an <c>@@hasInstance</c> hook that reads <c>nodeType</c>, so
/// the gap was narrower than it looks and also more confusing: <c>node instanceof Text</c> was
/// <see langword="true"/> while <c>node.constructor.name</c> was <c>"Object"</c> and
/// <c>Object.getPrototypeOf(node) === Text.prototype</c> was <see langword="false"/>. Debugging
/// output, logging and any dispatch keyed on the constructor all read the wrong thing.
/// </para>
/// <para>
/// <b>Elements are covered too.</b> They were left out because an element's interface is a tag
/// question and the table could not answer it: the entries overlapped (<c>HTMLMediaElement</c>
/// covered <c>audio</c> and <c>video</c> while <c>HTMLAudioElement</c> covered <c>audio</c> again,
/// so <c>audio</c> named two interfaces and a reverse lookup had none), and a tag the table omitted
/// had to fall back to something a browser splits three ways — a named interface, plain
/// <c>HTMLElement</c> for a known tag without one, and <c>HTMLUnknownElement</c> for a tag that is
/// neither. Guessing between them would have put a wrong name where an honest <c>"Object"</c> is at
/// least not misleading, so it was left. The answer was measured instead: every HTML tag run
/// through Chromium's own <c>createElement(tag).constructor.name</c>, the table made single-valued
/// with the abstract bases moved to an inheritance list, and the three-way fallback encoded from
/// what a browser does — including that a hyphenated name is an <c>HTMLElement</c> even when
/// nothing defined it. See <c>HtmlInterfaceForTag</c>.
/// </para>
/// <para>
/// <b>Moving the members onto those prototypes is a separate change, and it has reached elements.</b>
/// A character-data node's <c>Node</c>, <c>CharacterData</c> and <c>Text</c> members live on the
/// interface prototypes and are found through the receiver — see
/// <c>DomBridge/NodeInterfaces.cs</c>, where the mechanism that makes that possible is also
/// described. An element inherits the <c>Node.prototype</c> members bar <c>textContent</c>,
/// <c>Element</c>'s and, in the HTML namespace, <c>HTMLElement</c>'s (both above in this file); what
/// <c>DomBridge/JsObjects.cs</c> still puts on each wrapper is the rest — <c>textContent</c>, the
/// child mutations, the form-control reflectors and the per-tag members among it. The page's
/// document inherits <c>Node.prototype</c>'s members now that <c>DropDocumentNodeMemberCopies</c>
/// has deleted its own five and its constants; the rest of its surface, bar the routed
/// <c>EventTarget</c> three, stays its own. A doctype and a fragment still install most of the
/// <c>Node.prototype</c> members on themselves (<c>DomBridge/JsObjects.NonElementNodes.cs</c>).
/// </para>
/// <para>
/// Linking the prototype is a gain on its own, independently of which members moved: a page that
/// extends <c>Text.prototype</c> — the ordinary polyfill idiom — reaches instances.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>
    /// Points <paramref name="wrapper"/> at its interface prototype when the realm is up. A no-op
    /// otherwise, and for a node kind this does not name.
    /// </summary>
    /// <remarks>
    /// Both callers hold a handle: <c>DomBridge/JsObjects.cs</c> mints one through the realm and
    /// passes it straight here, and the re-link sweep at the end of
    /// <c>DomBridge/Registration/Registration.cs</c> reads them out of a registry that stores handles.
    /// </remarks>
    internal void ApplyInterfacePrototype(JsValue wrapper, DomNode node)
    {
        if (InterfaceNameFor(node) is { } interfaceName)
            LinkToInterface(wrapper, interfaceName);
    }

    /// <summary>
    /// Points <paramref name="wrapper"/> at <paramref name="interfaceName"/>'s prototype. The one
    /// seam for wrappers that are not minted from a <see cref="DomNode"/> — an attribute is not one
    /// in the canonical DOM, so its wrapper never reaches the node choke point.
    /// </summary>
    /// <remarks>
    /// A no-op before the realm exists, and for a name no interface global carries — the same two
    /// escapes the engine-typed lookup had, asked of the realm instead. The prototype is installed
    /// through <c>IJsMembers.SetPrototype</c>, which is the engine's <c>[[SetPrototypeOf]]</c> path
    /// and therefore retires the caches keyed on the chain the wrapper is leaving; assigning the
    /// backing field directly would link the object and leave those caches answering for the old
    /// chain.
    /// </remarks>
    internal void LinkToInterface(JsValue wrapper, string interfaceName)
    {
        if (_realm is not { } realm)
            return;

        var constructor = realm.GetProperty(realm.Global, interfaceName);
        if (!constructor.IsObject)
            return;

        var prototype = realm.GetProperty(constructor, "prototype");
        if (prototype.IsObject)
            realm.SetPrototype(wrapper, prototype);
    }
}
