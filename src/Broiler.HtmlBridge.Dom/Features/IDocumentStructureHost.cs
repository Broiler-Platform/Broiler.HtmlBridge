using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="DocumentStructureBinding"/> needs from the bridge: the document
/// root (whose element children are scanned for <c>&lt;body&gt;</c>/<c>&lt;head&gt;</c>), the
/// JS-wrapper factory, and the document title. Child enumeration is the bridge's neutral
/// <c>internal static</c> <c>ChildElements</c> helper, called directly.
/// </summary>
internal interface IDocumentStructureHost : IDocumentElementHost, IJsObjectHost
{
    string Title { get; set; }
}
