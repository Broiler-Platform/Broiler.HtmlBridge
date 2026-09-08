using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

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
    /// The wrapper is still the engine object the bridge's wrapper tables are keyed on — a handle
    /// carries it rather than copying it — so <c>document.body === document.body</c> is the same
    /// question it was before this contract changed vocabulary.
    /// </remarks>
    JsValue ToJsObject(DomNode node);

    DomElement DocumentElement { get; }
    string Title { get; set; }
}
