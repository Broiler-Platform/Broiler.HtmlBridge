using Broiler.Dom.Html;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    // RF-BRIDGE-1c Phase F4: HtmlTreeBuilder is retired. It used to re-materialize the shared
    // HtmlDocumentParser's canonical tree into facade Broiler.Dom.DomElement nodes; with the facade gone the
    // parser already produces the canonical nodes the bridge holds, so these helpers parse and hand
    // the tree straight back. Callers reparent the returned root/fragment children into the
    // _document-owned tree, and canonical AppendChild auto-adopts the subtree into _document (see
    // DomNode.InsertBefore), so no copy is needed. The returned AllElements list preserves the old
    // "non-structural nodes only" registration contract (html/head/body scaffold excluded).

    /// <summary>
    /// Parses a full HTML document via the shared <see cref="HtmlDocumentParser"/> and returns the
    /// canonical <c>&lt;html&gt;</c> root, the parsed <c>&lt;!DOCTYPE&gt;</c> node (or <c>null</c>),
    /// the non-structural node registration list, and the title. Replaces the retired
    /// <c>HtmlTreeBuilder.Build</c>.
    /// </summary>
    internal static (DomElement DocumentElement, DomDocumentType? DocumentType, List<DomNode> AllElements, string Title) BuildDocumentTree(string html)
    {
        var parsed = HtmlDocumentParser.ParseDocument(html);
        var root = parsed.Document.DocumentElement ??
            throw new InvalidOperationException("The shared HTML parser did not produce a document element.");

        var allElements = new List<DomNode>();
        AppendParsedTreeNodes(root, structural: true, allElements);
        return (root, parsed.Document.DocumentType, allElements, parsed.Title);
    }

    /// <summary>
    /// Parses an HTML fragment in <paramref name="contextTagName"/>'s context and wraps its children
    /// in a canonical <see cref="DomDocumentFragment"/> (Phase 4 item 1 — was a <c>#document-fragment</c>
    /// sentinel element). Replaces <c>HtmlTreeBuilder.BuildFragment</c>.
    /// </summary>
    private (DomDocumentFragment Fragment, List<DomNode> AllElements) BuildFragmentTree(string html, string contextTagName)
    {
        var parsed = HtmlDocumentParser.ParseFragment(html, contextTagName);
        var fragment = CreateBridgeDocumentFragment();
        var allElements = new List<DomNode>();

        // AppendChild adopts each parsed child subtree into _document (fragment is _document-owned).
        foreach (var child in parsed.Fragment.ChildNodes.ToArray())
        {
            fragment.AppendChild(child);
            AppendParsedTreeNodes(child, structural: false, allElements);
        }

        return (fragment, allElements);
    }

    /// <summary>
    /// Collects the registration set in document order, excluding the structural scaffold
    /// (<c>&lt;html&gt;</c> and its direct <c>&lt;head&gt;</c>/<c>&lt;body&gt;</c> children) exactly as the
    /// retired <c>HtmlTreeBuilder.ConvertNode</c>'s <c>structural</c> flag did.
    /// </summary>
    private static void AppendParsedTreeNodes(DomNode source, bool structural, List<DomNode> allElements)
    {
        if (!structural)
            allElements.Add(source);

        foreach (var child in source.ChildNodes)
        {
            var childIsStructural = structural &&
                child is DomElement childElement &&
                childElement.LocalName is "head" or "body";
            
            AppendParsedTreeNodes(child, childIsStructural, allElements);
        }
    }
}
