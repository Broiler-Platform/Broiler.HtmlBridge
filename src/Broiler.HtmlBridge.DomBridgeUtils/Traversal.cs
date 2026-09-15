using Broiler.Dom;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    /// <summary>
    /// Returns the top-level nodes overlapping the given range boundaries, for the range
    /// client-rect geometry. For a single container this is the children between the offsets;
    /// across containers it is the document-order nodes strictly between start and end, keeping
    /// only those not already covered by an included ancestor. This is a client-rect geometry
    /// heuristic — it includes partially-overlapping elements, unlike the spec-strict
    /// <c>DomRange.IsContained</c> set — so it stays bridge-owned rather than promoting to
    /// canonical Broiler.Dom. It reuses canonical <see cref="DomNode.InclusiveDescendants"/> for
    /// the document-order walk.
    /// </summary>
    internal static List<DomNode> GetNodesInRange(DomNode startContainer, int startOffset, DomNode endContainer, int endOffset)
    {
        var result = new List<DomNode>();
        if (ReferenceEquals(startContainer, endContainer))
        {
            // Same container — return children between offsets
            for (var i = startOffset; i < Math.Min(endOffset, startContainer.ChildNodes.Count); i++)
                result.Add(ChildAt(startContainer, i));
            return result;
        }

        // Different containers — collect nodes between start and end
        var ancestor = startContainer.CommonAncestorWith(endContainer);
        if (ancestor == null) return result;

        var allNodes = ancestor.InclusiveDescendants().ToList();
        var startIdx = allNodes.IndexOf(startContainer);
        var endIdx = allNodes.IndexOf(endContainer);
        if (startIdx < 0 || endIdx < 0) return result;

        for (var i = startIdx + 1; i < endIdx; i++)
        {
            var node = allNodes[i];
            // Only include top-level nodes (not descendants of already-included nodes)
            var isDescendantOfIncluded = result.Any(r => node.IsDescendantOf(r));
            if (!isDescendantOfIncluded)
                result.Add(node);
        }
        return result;
    }

    internal static void UpdateCharacterData(DomNode target, string? newValue) => SetBridgeText(target, newValue ?? string.Empty);
}
