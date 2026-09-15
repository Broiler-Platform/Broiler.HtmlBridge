using Broiler.Dom;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
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
    internal static string? InterfaceNameFor(DomNode node) => node switch
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
    internal static bool IsHtmlNamespace(DomElement element) =>
        element.NamespaceUri is null or "" or "http://www.w3.org/1999/xhtml";
}
