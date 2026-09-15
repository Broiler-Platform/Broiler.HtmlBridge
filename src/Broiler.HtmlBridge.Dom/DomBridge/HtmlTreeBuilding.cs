using Broiler.Dom.Html;
using Broiler.Dom;
using static Broiler.HtmlBridge.DomBridgeUtils;

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
}
