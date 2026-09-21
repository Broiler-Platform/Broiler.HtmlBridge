using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="DocumentWriteBinding"/> needs from the bridge: the document
/// root and the document-order element list plus the current parser insertion point (so a written
/// fragment lands right after the executing <c>&lt;script&gt;</c>, matching the parser insertion
/// point).
/// </summary>
internal interface IDocumentWriteHost : IDocumentElementHost, IElementsHost
{
    int CurrentScriptIndex { get; }
}
