using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="DocumentStructureBinding"/> needs from the bridge: the document
/// root (whose element children are scanned for <c>&lt;body&gt;</c>/<c>&lt;head&gt;</c>), the
/// JS-wrapper factory, and the document title. Child enumeration is the bridge's neutral
/// <c>internal static</c> <c>ChildElements</c> helper, called directly.
/// </summary>
internal interface IDocumentStructureHost
{
    /// <summary>
    /// The single JS wrapper identity for <paramref name="node"/>, as a JSEAL handle.
    /// </summary>
    /// <remarks>
    /// The bridge answers the handle it caches for the node, and that handle carries the same object on
    /// every call, so <c>document.body === document.body</c>.
    /// </remarks>
    JsValue ToJsObject(DomNode node);

    DomElement DocumentElement { get; }
    string Title { get; set; }
}
