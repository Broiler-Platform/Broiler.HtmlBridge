using Broiler.Dom;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

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
/// <b>Moving the members onto those prototypes is a separate change, and it has started.</b> A
/// character-data node's <c>Node</c>, <c>CharacterData</c> and <c>Text</c> members live on the
/// interface prototypes and are found through the receiver — see
/// <c>DomBridge.CharacterDataInterface.cs</c>, where the mechanism that makes that possible is also
/// described. An element and a document still install their interface as own properties of each
/// wrapper, so <c>Object.getOwnPropertyNames(element)</c> still lists all 166 of them; that is the
/// rest of the item.
/// <para>
/// Linking the prototype was a real gain on its own even before any member moved: a page that
/// extends <c>Text.prototype</c> — the ordinary polyfill idiom — reaches instances, where before the
/// assignment went to an object nothing inherited from.
/// </para>
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>
    /// Points <paramref name="wrapper"/> at its interface prototype when the realm is up. A no-op
    /// otherwise, and for a node kind this does not name.
    /// </summary>
    internal void ApplyInterfacePrototype(JSObject wrapper, DomNode node)
    {
        if (InterfaceNameFor(node) is { } interfaceName)
            LinkToInterface(wrapper, interfaceName);
    }

    /// <summary>
    /// Points <paramref name="wrapper"/> at <paramref name="interfaceName"/>'s prototype. The one
    /// seam for wrappers that are not minted from a <see cref="DomNode"/> — an attribute is not one
    /// in the canonical DOM, so its wrapper never reaches the node choke point.
    /// </summary>
    internal void LinkToInterface(JSObject wrapper, string interfaceName)
    {
        if (_jsContext?[interfaceName] is JSObject constructor &&
            constructor[(KeyString)"prototype"] is JSObject prototype)
            wrapper.BasePrototypeObject = prototype;
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
