using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

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
/// <c>DomBridge/CharacterDataInterface.cs</c>, where the mechanism that makes that possible is also
/// described. An element inherits the <c>Node.prototype</c> members bar <c>textContent</c>,
/// <c>Element</c>'s (<c>DomBridge/ElementInterface.cs</c>) and, in the HTML namespace,
/// <c>HTMLElement</c>'s (<c>DomBridge/HtmlElementInterface.cs</c>); what
/// <c>DomBridge/JsObjects.cs</c> still puts on each wrapper is the rest — <c>textContent</c>, the
/// child mutations, the form-control reflectors and the per-tag members among it. The page's
/// document inherits <c>Node.prototype</c>'s members now that <c>DropDocumentNodeMemberCopies</c>
/// has deleted its own five and its constants; the rest of its surface, bar the routed
/// <c>EventTarget</c> three, stays its own. A doctype and a fragment still install most of the
/// <c>Node.prototype</c> members on themselves (<c>DomBridge/JsObjects.NonElementNodes.cs</c>).
/// (This said an element and a document still installed their whole interface, 166 members for
/// an element.)
/// </para>
/// <para>
/// Linking the prototype was a real gain on its own even before any member moved: a page that
/// extends <c>Text.prototype</c> — the ordinary polyfill idiom — reaches instances, where before the
/// assignment went to an object nothing inherited from.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>
    /// Points <paramref name="wrapper"/> at its interface prototype when the realm is up. A no-op
    /// otherwise, and for a node kind this does not name.
    /// </summary>
    /// <remarks>
    /// It was engine-typed because the wrapper factories that call it were: each held the wrapper it
    /// had just minted as the engine's own object. Both callers hold a handle now:
    /// <c>DomBridge/JsObjects.cs</c> mints one through the realm and passes it straight here, and
    /// the re-link sweep at the end of <c>DomBridge/Registration/Registration.cs</c> reads them
    /// out of a registry that stores handles. <c>LinkToInterface</c> below is the only one there
    /// is; the engine-typed overload it used to sit beside is gone.
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

    /// <summary>
    /// The interface a node implements, or <see langword="null"/> for a kind this does not reach.
    /// </summary>
    /// <remarks>
    /// The element arm is a tag lookup rather than a type test, because that is what an element's
    /// interface is; <see cref="HtmlInterfaceForTag"/> owns the rule. A non-HTML element — an SVG one
    /// — is deliberately left at <c>SVGElement</c> rather than given a per-tag name: a browser does
    /// have <c>SVGRectElement</c> and the rest, but this engine registers no SVG element interfaces
    /// to point at, and inventing the globals to satisfy a name is what the collection work already
    /// ruled out.
    /// <para>
    /// The order matters: <see cref="DomDocument"/> is checked before the element arm because a
    /// document is not an element, and <c>HTMLDocument</c> is its interface.
    /// </para>
    /// </remarks>
    private static string? InterfaceNameFor(DomNode node) => node switch
    {
        DomDocumentType => "DocumentType",
        DomDocumentFragment => "DocumentFragment",
        DomComment => "Comment",
        DomText => "Text",
        DomDocument => "HTMLDocument",
        DomElement element => IsHtmlNamespace(element) ? HtmlInterfaceForTag(element.TagName) : "SVGElement",
        _ => null,
    };

    /// <summary>Whether the element is in the HTML namespace — including the no-namespace case, which
    /// a bridge element created outside a namespace-aware path reports and which the
    /// <c>instanceof</c> hooks already treat as HTML.</summary>
    private static bool IsHtmlNamespace(DomElement element) =>
        element.NamespaceUri is null or "" or "http://www.w3.org/1999/xhtml";
}
