using Broiler.Dom.Html;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
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
    /// Collects the registration set in document order, excluding the structural scaffold
    /// (<c>&lt;html&gt;</c> and its direct <c>&lt;head&gt;</c>/<c>&lt;body&gt;</c> children) exactly as the
    /// retired <c>HtmlTreeBuilder.ConvertNode</c>'s <c>structural</c> flag did.
    /// </summary>
    internal static void AppendParsedTreeNodes(DomNode source, bool structural, List<DomNode> allElements)
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
