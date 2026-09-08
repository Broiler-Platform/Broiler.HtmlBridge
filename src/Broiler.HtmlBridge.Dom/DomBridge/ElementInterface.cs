using System.Runtime.CompilerServices;

using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

/// <summary>
/// <c>Element</c> as a real interface: its members on <c>Element.prototype</c>, found through the
/// receiver, rather than copied onto every element wrapper in the document.
/// </summary>
/// <remarks>
/// <para>
/// This is the element half of track 6's wrapper item, and it follows the character-data move
/// (<c>DomBridge.CharacterDataInterface.cs</c>, which describes the receiver mechanism) and the
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
/// and the <c>offset*</c> metrics are <c>HTMLElement</c>'s and stay on the instance until that
/// interface moves; <c>appendChild</c> and the other four tree mutations are <c>Node</c>'s;
/// <c>textContent</c> is <c>Node</c>'s and deliberately the element's own (its operation differs from
/// a character-data node's); and <c>data</c>, <c>length</c>, <c>scrollParent</c> and
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
/// <b>The installer speaks JSEAL, and the engine vocabulary that is left is a list of unmigrated
/// neighbours.</b> Members are minted through <see cref="Realm"/> and installed on a handle over the
/// target, which is a cast rather than a conversion — so each lands on the object in the position it
/// is written in and <c>Object.getOwnPropertyNames</c> reports the order it always did, with the
/// migrated and unmigrated members interleaved exactly as below. What still needs the engine's
/// argument frame is named where it appears: <c>animate</c> (<c>DomBridge/WebAnimations.cs</c>), the
/// four <c>ChildNode</c> members (<see cref="Dom.Features.ChildNodeBinding"/>, whose bodies are
/// engine-framed because two unmigrated files install them elsewhere), <see cref="_dialogs"/>, and the
/// two entry points below that are handed an engine object by <c>DomBridge/JsObjects.cs</c> and
/// <c>DomBridge/CharacterDataInterface.cs</c>.
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
        if (PrototypeOfInterface("Element") is not { } proto)
            return;

        InstallElementInterface(
            Dom.Runtime.JsInterop.FromEngineObject(proto), RequireElementReceiver, RequireWrapperReceiver);
        _elementInterfacePrototypeReady = true;
    }

    /// <summary>
    /// <c>Element</c>'s members as own properties of one wrapper — the shape every element had before
    /// they moved, kept for the one case that cannot use the prototype: a wrapper minted before the
    /// realm carried the interfaces, which inherits from nothing.
    /// </summary>
    /// <remarks>
    /// Engine-typed because its caller is: <c>DomBridge/JsObjects.cs</c> mints the wrapper and holds
    /// it as the engine's own object. The seam is a cast, so the handle below is that object.
    /// </remarks>
    private void PopulateElementInterfaceOnInstance(JSObject obj, DomElement element)
    {
        var wrapper = Dom.Runtime.JsInterop.FromEngineObject(obj);
        InstallElementInterface(wrapper, (in JsCall _, string _) => element, (in JsCall _, string _) => wrapper);
    }

    /// <summary>The element the receiver names, or a <c>TypeError</c> when it is not one.</summary>
    /// <remarks>
    /// A browser answers the same for a receiver of the wrong kind — <c>Element.prototype.getAttribute
    /// .call(document, 'x')</c> and <c>.call(textNode, 'x')</c> are both illegal invocations, because
    /// neither implements <c>Element</c> however node-like it is.
    /// </remarks>
    private DomElement RequireElementReceiver(in JsCall call, string member)
    {
        // The wrapper registry is keyed on the engine's own objects and has not migrated, so the
        // handle is unwrapped to ask it. A non-object receiver never reaches that: it answers the
        // same TypeError the engine-object test used to.
        if (call.This.IsObject &&
            _jsObjects.TryGetNode(Dom.Runtime.JsInterop.ToEngineObject(call.This), out var node) &&
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
            _jsObjects.TryGetNode(Dom.Runtime.JsInterop.ToEngineObject(call.This), out var node) &&
            node is DomElement)
        {
            return call.This;
        }

        throw call.Realm.Error(JsErrorKind.TypeError,
            $"Failed to execute '{member}' on 'Element': Illegal invocation");
    }

    /// <summary>
    /// A <see cref="Dom.Features.JsElementSource"/> as the engine-shaped
    /// <see cref="Dom.Features.ElementSource"/>, for the feature modules that still install their
    /// members on an engine argument frame.
    /// </summary>
    /// <remarks>
    /// One resolution rule, asked through whichever frame the member happens to have — this is the
    /// mirror of the <c>JsSourceOf</c> that used to point the other way, and it exists for the same
    /// reason. Building a second receiver-resolving source against the engine's frame would work and is
    /// exactly what must not happen: the prototype's members and a pre-realm wrapper's are the same
    /// members because one installer writes them, and two sources answering "which element is this"
    /// independently is the drift that arrangement exists to prevent.
    /// <para>
    /// Both sources look at the receiver and nothing else (<see cref="RequireElementReceiver"/> tests
    /// <c>call.This</c>; the capturing source ignores the frame entirely), so presenting the engine
    /// frame's receiver as a receiver-only <see cref="JsCall"/> asks each of them exactly the question
    /// it answers — including the <c>TypeError</c> a receiver that is not an element still raises. A
    /// receiver that is not an engine object becomes <c>undefined</c>, which fails the same test the
    /// engine-object one did.
    /// </para>
    /// </remarks>
    private Dom.Features.ElementSource EngineSourceOf(Dom.Features.JsElementSource element) =>
        (in Arguments a, string member) =>
        {
            var receiver = a.This is JSObject wrapper
                ? Dom.Runtime.JsInterop.FromEngineObject(wrapper)
                : JsValue.Undefined;
            var call = new JsCall(Realm, receiver, default);
            return element(in call, member);
        };

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

        // The dialog/details/popover module and animate() still install against the engine's own
        // object and read its argument frame, so each is handed both — the same object this file has
        // been installing on, and the same resolution rule under the frame its members read. The
        // adapted source is built once here rather than per call.
        var engineTarget = Dom.Runtime.JsInterop.ToEngineObject(target);
        var engineElement = EngineSourceOf(element);

        _dialogs.InstallElementMembers(engineTarget, engineElement);

        // Animatable.animate() — Web Animations §Animatable, which Element includes. ElementAnimate is
        // the bridge's own unmigrated callback (DomBridge/WebAnimations.cs) and reads the engine frame.
        AddPrototypeMethod(engineTarget, "animate", 2,
            (in Arguments a) => ElementAnimate(engineElement(in a, "animate"), in a));
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
            JsValue.Number(ChildElements(element(in call, "childElementCount")).Count(c => !IsText(c))));
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

        // The ChildNode mixin is the one group here whose bodies are still engine-framed, and not
        // because of this file: DomBridge/CharacterDataInterface.cs and
        // DomBridge/JsObjects.NonElementNodes.cs install the same four members on Node.prototype and
        // on the non-element wrappers, so ChildNodeBinding reads the engine's frame for all three
        // callers. Writing a second, JSEAL-framed copy of those four bodies to serve this one is the
        // duplication the shared installer above exists to avoid, so the engine frame is adapted here
        // instead and the four move together when those two files do.
        var engineTarget = Dom.Runtime.JsInterop.ToEngineObject(target);
        var engineElement = EngineSourceOf(element);

        AddPrototypeMethod(engineTarget, "remove", 0,
            (in Arguments a) => Dom.Features.ChildNodeBinding.Remove(this, engineElement(in a, "remove"), in a));
        AddPrototypeMethod(engineTarget, "before", 0,
            (in Arguments a) => Dom.Features.ChildNodeBinding.Before(this, engineElement(in a, "before"), in a));
        AddPrototypeMethod(engineTarget, "after", 0,
            (in Arguments a) => Dom.Features.ChildNodeBinding.After(this, engineElement(in a, "after"), in a));
        AddPrototypeMethod(engineTarget, "replaceWith", 0,
            (in Arguments a) => Dom.Features.ChildNodeBinding.ReplaceWith(this, engineElement(in a, "replaceWith"), in a));
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

    /// <summary>
    /// Argument zero as a string — the ECMAScript coercion, which may run a <c>toString</c> the page
    /// wrote — or the empty string when nothing was passed.
    /// </summary>
    /// <remarks>
    /// The selector and collection members read their argument here rather than inside
    /// <see cref="Dom.Features.SelectorsBinding"/>, because the module's entry points take the string
    /// their caller has already produced — the sub-document and <c>DocumentFragment</c> forms share
    /// them. It is the realm's <c>ToString</c> and not the handle's rendering, which is the same read
    /// on the same value the engine frame performed.
    /// </remarks>
    private static string StringArgument(in JsCall call) =>
        call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;

    /// <summary>
    /// <c>tagName</c>'s value: upper-cased for an HTML element, verbatim otherwise, which is the rule
    /// the wrapper applied once when it minted the string.
    /// </summary>
    private static string TagNameForScript(DomElement element) =>
        string.IsNullOrEmpty(element.NamespaceUri) ||
        string.Equals(element.NamespaceUri, "http://www.w3.org/1999/xhtml", StringComparison.OrdinalIgnoreCase)
            ? element.TagName.ToUpperInvariant()
            : element.TagName;

    /// <summary>The element's one <c>DOMTokenList</c>, built on first use.</summary>
    private JsValue ClassListFor(DomElement element) =>
        _classLists.GetValue(element, key => new StrongBox<JsValue>(
            Dom.Features.ClassListBinding.Build(Realm, key, InvalidateStyleScope))).Value;
}
