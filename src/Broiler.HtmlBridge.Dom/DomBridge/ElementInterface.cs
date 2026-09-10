using System.Runtime.CompilerServices;

using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
// The engine namespaces are here for one member: animate()'s body reads the engine's own argument
// frame (DomBridge/WebAnimations.cs), so the member has to be minted with that frame — see
// AddPrototypeMethod, which this file also lends to DomBridge/HtmlElementInterface.cs for
// click/focus/blur.

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
/// <b>The installer speaks JSEAL, and one member is what is left of the engine vocabulary.</b> Every
/// member is minted through <see cref="Realm"/> and installed on the target handle, in the position it
/// is written in, so <c>Object.getOwnPropertyNames</c> reports the order it always did. The exception
/// is <c>animate</c>: its body is <c>DomBridge/WebAnimations.cs</c>'s <see cref="ElementAnimate"/>,
/// which reads the engine's own argument frame and parses the keyframes and the options object out of
/// it. There is no adapter between two call frames — only between two object types — so that one
/// member is minted with the frame its body reads, and <see cref="ElementForEngineReceiver"/> asks the
/// single element source below through it. The four <c>ChildNode</c> members and the fullscreen pair
/// were engine-framed for the same reason and are not any more: their modules read a
/// <see cref="JsCall"/>.
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
        // The wrapper registry is keyed on the engine's own objects and has not migrated, so the
        // handle is unwrapped to ask it. A non-object receiver never reaches that: it answers the
        // same TypeError the engine-object test used to.
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

        // The ChildNode mixin. These four bodies were the last engine-framed group in this file, and
        // not because of this file: DomBridge/CharacterDataInterface.cs and
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
