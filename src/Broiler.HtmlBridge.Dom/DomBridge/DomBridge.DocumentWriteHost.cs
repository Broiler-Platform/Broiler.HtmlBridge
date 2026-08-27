namespace Broiler.HtmlBridge;

// Explicit IDocumentWriteHost implementation for the DocumentWriteBinding feature module (Phase 3):
// the bridge exposes the document root, the document-order element list, the current parser
// insertion point, and the HTML-fragment parser, via explicit interface members so the module
// never reaches an arbitrary bridge private field and the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IDocumentWriteHost
{
    Broiler.Dom.DomElement Dom.Features.IDocumentWriteHost.DocumentElement => DocumentElement;

    IReadOnlyList<Broiler.Dom.DomElement> Dom.Features.IDocumentWriteHost.Elements => Elements;

    int Dom.Features.IDocumentWriteHost.CurrentScriptIndex => CurrentScriptIndex;

    Broiler.Dom.DomDocumentFragment Dom.Features.IDocumentWriteHost.BuildFragment(string html, string contextTagName)
        => BuildFragmentTree(html, contextTagName).Fragment;
}
