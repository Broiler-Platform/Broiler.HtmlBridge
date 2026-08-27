using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="DocumentWriteBinding"/> needs from the bridge: the document
/// root and the document-order element list plus the current parser insertion point (so a written
/// fragment lands right after the executing <c>&lt;script&gt;</c>, matching the parser insertion
/// point), and the HTML-fragment parser itself.
/// </summary>
internal interface IDocumentWriteHost
{
    DomElement DocumentElement { get; }
    IReadOnlyList<DomElement> Elements { get; }
    int CurrentScriptIndex { get; }

    /// <summary>Parses an HTML fragment in the given context element and returns its fragment root.</summary>
    DomDocumentFragment BuildFragment(string html, string contextTagName);
}
