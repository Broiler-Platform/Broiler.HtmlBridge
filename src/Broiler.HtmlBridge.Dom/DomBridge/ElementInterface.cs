using System.Runtime.CompilerServices;

using Broiler.Dom;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

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
/// <b>One installer serves both places.</b> Each member is written once, against an
/// <see cref="Dom.Features.ElementSource"/> that answers either the element captured when the wrapper
/// was built or the element the receiver names. The prototype gets the receiver-resolving source and
/// a wrapper minted before the realm exists — which inherits from nothing — gets the capturing one.
/// The two cannot drift, which is what the earlier moves had to establish by reading every copy
/// against its prototype counterpart by hand.
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
    /// has dropped it.
    /// </remarks>
    private readonly ConditionalWeakTable<DomElement, JSObject> _classLists = new();

    /// <summary>
    /// Installs <c>Element</c>'s members on <c>Element.prototype</c>. A no-op when the realm does not
    /// carry the interface.
    /// </summary>
    internal void RegisterElementInterface()
    {
        if (PrototypeOfInterface("Element") is not { } proto)
            return;

        InstallElementInterface(proto, RequireElementReceiver, RequireWrapperReceiver);
        _elementInterfacePrototypeReady = true;
    }

    /// <summary>
    /// <c>Element</c>'s members as own properties of one wrapper — the shape every element had before
    /// they moved, kept for the one case that cannot use the prototype: a wrapper minted before the
    /// realm carried the interfaces, which inherits from nothing.
    /// </summary>
    private void PopulateElementInterfaceOnInstance(JSObject obj, DomElement element)
    {
        InstallElementInterface(obj, (in Arguments _, string _) => element, (in Arguments _, string _) => obj);
    }

    /// <summary>The element the receiver names, or a <c>TypeError</c> when it is not one.</summary>
    /// <remarks>
    /// A browser answers the same for a receiver of the wrong kind — <c>Element.prototype.getAttribute
    /// .call(document, 'x')</c> and <c>.call(textNode, 'x')</c> are both illegal invocations, because
    /// neither implements <c>Element</c> however node-like it is.
    /// </remarks>
    private DomElement RequireElementReceiver(in Arguments a, string member)
    {
        if (a.This is JSObject receiver && _jsObjects.TryGetNode(receiver, out var node) && node is DomElement element)
            return element;

        return JSException.ThrowTypeError<DomElement>(
            $"Failed to execute '{member}' on 'Element': Illegal invocation");
    }

    /// <summary>The receiver itself, once it is known to be an element wrapper.</summary>
    private JSObject RequireWrapperReceiver(in Arguments a, string member)
    {
        if (a.This is JSObject receiver && _jsObjects.TryGetNode(receiver, out var node) && node is DomElement)
            return receiver;

        return JSException.ThrowTypeError<JSObject>(
            $"Failed to execute '{member}' on 'Element': Illegal invocation");
    }

    /// <summary>
    /// The whole <c>Element</c> interface onto <paramref name="target"/> — <c>Element.prototype</c>,
    /// or one wrapper when there is no prototype to inherit from.
    /// </summary>
    /// <remarks>
    /// The property attributes are the ones the wrapper always used and the ones Web IDL asks for on a
    /// prototype — enumerable and configurable — so a member's <em>location</em> is the only thing this
    /// change moves.
    /// </remarks>
    private void InstallElementInterface(JSObject target, Dom.Features.ElementSource element, Dom.Features.WrapperSource wrapper)
    {
        InstallElementIdentityMembers(target, element);
        InstallElementAttributeMembers(target, element, wrapper);
        InstallElementContentMembers(target, element);
        InstallElementTreeMembers(target, element);
        InstallElementSelectionMembers(target, element);

        Dom.Features.ElementGeometryBinding.InstallElementMembers(this, target, element);
        _dialogs.InstallElementMembers(target, element);

        // Animatable.animate() — Web Animations §Animatable, which Element includes.
        AddPrototypeMethod(target, "animate", 2,
            (in Arguments a) => ElementAnimate(element(in a, "animate"), in a));
    }

    /// <summary>
    /// <c>tagName</c>, the reflected <c>id</c>/<c>className</c>, <c>classList</c> and the shadow-host
    /// pair.
    /// </summary>
    private void InstallElementIdentityMembers(JSObject target, Dom.Features.ElementSource element)
    {
        // tagName is an accessor here where the wrapper installed a JSString fixed when it was built.
        // That was the "per-instance value" half of the item: a captured value cannot serve a
        // prototype, and a browser's tagName is an accessor in any case.
        AddPrototypeAccessor(target, "tagName",
            (in Arguments a) => new JSString(TagNameForScript(element(in a, "tagName"))));

        Dom.Features.GlobalAttributeBinding.InstallElementMembers(this, target, element);

        // classList — one DOMTokenList per element, memoized so identity holds (see _classLists).
        AddPrototypeAccessor(target, "classList",
            (in Arguments a) => ClassListFor(element(in a, "classList")));

        AddPrototypeAccessor(target, "shadowRoot",
            (in Arguments a) => Dom.Features.ShadowDomBinding.GetShadowRoot(this, element(in a, "shadowRoot"), in a));
        AddPrototypeMethod(target, "attachShadow", 1,
            (in Arguments a) => Dom.Features.ShadowDomBinding.AttachShadow(this, element(in a, "attachShadow"), in a));
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
    private void InstallElementAttributeMembers(JSObject target, Dom.Features.ElementSource element, Dom.Features.WrapperSource wrapper)
    {
        AddPrototypeAccessor(target, "attributes", (in Arguments a) =>
            _attributes.BuildNamedNodeMap(element(in a, "attributes"), wrapper(in a, "attributes")));

        AddPrototypeMethod(target, "getAttribute", 1,
            (in Arguments a) => _attributes.GetAttribute(element(in a, "getAttribute"), in a));
        AddPrototypeMethod(target, "getAttributeNS", 2,
            (in Arguments a) => _attributes.GetAttributeNS(element(in a, "getAttributeNS"), in a));
        AddPrototypeMethod(target, "getAttributeNames", 0, (in Arguments a) =>
            new JSArray([.. AttributeNames(element(in a, "getAttributeNames")).Select(static name => (JSValue)new JSString(name))]));

        AddPrototypeMethod(target, "setAttribute", 2,
            (in Arguments a) => _attributes.SetAttribute(element(in a, "setAttribute"), in a));
        AddPrototypeMethod(target, "setAttributeNS", 3,
            (in Arguments a) => _attributes.SetAttributeNS(element(in a, "setAttributeNS"), in a));

        AddPrototypeMethod(target, "removeAttribute", 1,
            (in Arguments a) => _attributes.RemoveAttribute(element(in a, "removeAttribute"), in a));
        AddPrototypeMethod(target, "removeAttributeNS", 2,
            (in Arguments a) => _attributes.RemoveAttributeNS(element(in a, "removeAttributeNS"), in a));
        AddPrototypeMethod(target, "toggleAttribute", 2,
            (in Arguments a) => _attributes.ToggleAttribute(element(in a, "toggleAttribute"), in a));

        AddPrototypeMethod(target, "hasAttribute", 1,
            (in Arguments a) => _attributes.HasAttribute(element(in a, "hasAttribute"), in a));
        AddPrototypeMethod(target, "hasAttributeNS", 2,
            (in Arguments a) => _attributes.HasAttributeNS(element(in a, "hasAttributeNS"), in a));
        AddPrototypeMethod(target, "hasAttributes", 0, (in Arguments a) =>
            element(in a, "hasAttributes").Attributes.Count > 0 ? JSBoolean.True : JSBoolean.False);

        AddPrototypeMethod(target, "getAttributeNode", 1, (in Arguments a) =>
            _attributes.GetAttributeNode(element(in a, "getAttributeNode"), wrapper(in a, "getAttributeNode"), in a));
        AddPrototypeMethod(target, "getAttributeNodeNS", 2, (in Arguments a) =>
            _attributes.GetAttributeNodeNS(element(in a, "getAttributeNodeNS"), wrapper(in a, "getAttributeNodeNS"), in a));
        AddPrototypeMethod(target, "setAttributeNode", 1, (in Arguments a) =>
            _attributes.SetAttributeNode(element(in a, "setAttributeNode"), wrapper(in a, "setAttributeNode"), in a));
        AddPrototypeMethod(target, "setAttributeNodeNS", 1, (in Arguments a) =>
            _attributes.SetAttributeNodeNS(element(in a, "setAttributeNodeNS"), wrapper(in a, "setAttributeNodeNS"), in a));
        AddPrototypeMethod(target, "removeAttributeNode", 1, (in Arguments a) =>
            _attributes.RemoveAttributeNode(element(in a, "removeAttributeNode"), wrapper(in a, "removeAttributeNode"), in a));
    }

    /// <summary>The markup members: <c>innerHTML</c>/<c>outerHTML</c> and the three adjacent inserts.</summary>
    private void InstallElementContentMembers(JSObject target, Dom.Features.ElementSource element)
    {
        Dom.Features.ElementContentBinding.InstallHtmlSerialization(this, target, element);
        Dom.Features.InsertAdjacentBinding.Install(this, target, element);
    }

    /// <summary>
    /// The tree members <c>Element</c> carries: the <c>ParentNode</c> element views and inserts, the
    /// <c>ChildNode</c> mixin, and the two <c>NonDocumentTypeChildNode</c> siblings.
    /// </summary>
    private void InstallElementTreeMembers(JSObject target, Dom.Features.ElementSource element)
    {
        AddPrototypeAccessor(target, "children",
            (in Arguments a) => Dom.Features.ElementTraversalBinding.GetChildren(this, element(in a, "children"), in a));
        AddPrototypeAccessor(target, "childElementCount", (in Arguments a) =>
            new JSNumber(ChildElements(element(in a, "childElementCount")).Count(c => !IsText(c))));
        AddPrototypeAccessor(target, "firstElementChild",
            (in Arguments a) => Dom.Features.ElementTraversalBinding.GetFirstElementChild(this, element(in a, "firstElementChild"), in a));
        AddPrototypeAccessor(target, "lastElementChild",
            (in Arguments a) => Dom.Features.ElementTraversalBinding.GetLastElementChild(this, element(in a, "lastElementChild"), in a));
        AddPrototypeAccessor(target, "nextElementSibling",
            (in Arguments a) => Dom.Features.ElementTraversalBinding.GetNextElementSibling(this, element(in a, "nextElementSibling"), in a));
        AddPrototypeAccessor(target, "previousElementSibling",
            (in Arguments a) => Dom.Features.ElementTraversalBinding.GetPreviousElementSibling(this, element(in a, "previousElementSibling"), in a));

        AddPrototypeMethod(target, "append", 0,
            (in Arguments a) => Dom.Features.TreeMutationBinding.Append(this, element(in a, "append"), in a));
        AddPrototypeMethod(target, "prepend", 0,
            (in Arguments a) => Dom.Features.TreeMutationBinding.Prepend(this, element(in a, "prepend"), in a));
        // replaceChildren is the ParentNode member the wrapper never had: the document's has been
        // here since the mixin was bound there, and an element's — the commoner one, since
        // `container.replaceChildren()` is how a page empties a node — threw as undefined.
        AddPrototypeMethod(target, "replaceChildren", 0,
            (in Arguments a) => Dom.Features.TreeMutationBinding.ReplaceChildren(this, element(in a, "replaceChildren"), in a));

        AddPrototypeMethod(target, "remove", 0,
            (in Arguments a) => Dom.Features.ChildNodeBinding.Remove(this, element(in a, "remove"), in a));
        AddPrototypeMethod(target, "before", 0,
            (in Arguments a) => Dom.Features.ChildNodeBinding.Before(this, element(in a, "before"), in a));
        AddPrototypeMethod(target, "after", 0,
            (in Arguments a) => Dom.Features.ChildNodeBinding.After(this, element(in a, "after"), in a));
        AddPrototypeMethod(target, "replaceWith", 0,
            (in Arguments a) => Dom.Features.ChildNodeBinding.ReplaceWith(this, element(in a, "replaceWith"), in a));
    }

    /// <summary>The selector and collection lookups scoped to an element.</summary>
    private void InstallElementSelectionMembers(JSObject target, Dom.Features.ElementSource element)
    {
        AddPrototypeMethod(target, "querySelector", 1,
            (in Arguments a) => Dom.Features.SelectorsBinding.QuerySelector(this, element(in a, "querySelector"), in a));
        AddPrototypeMethod(target, "querySelectorAll", 1,
            (in Arguments a) => Dom.Features.SelectorsBinding.QuerySelectorAll(this, element(in a, "querySelectorAll"), in a));
        AddPrototypeMethod(target, "matches", 1,
            (in Arguments a) => Dom.Features.SelectorsBinding.Matches(this, element(in a, "matches"), in a));
        AddPrototypeMethod(target, "closest", 1,
            (in Arguments a) => Dom.Features.SelectorsBinding.Closest(this, element(in a, "closest"), in a));
        AddPrototypeMethod(target, "getElementsByTagName", 1,
            (in Arguments a) => Dom.Features.SelectorsBinding.GetElementsByTagName(this, element(in a, "getElementsByTagName"), in a));
        AddPrototypeMethod(target, "getElementsByClassName", 1,
            (in Arguments a) => Dom.Features.SelectorsBinding.GetElementsByClassName(this, element(in a, "getElementsByClassName"), in a));
    }

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
    private JSObject ClassListFor(DomElement element) =>
        _classLists.GetValue(element, key => Dom.Features.ClassListBinding.Build(key, InvalidateStyleScope));
}
