using Broiler.Dom;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    // The nested-browsing-context `document` object surface (BuildSubDocument + every
    // JsSubDocumentObjects* callback) was extracted into the co-located
    // Broiler.HtmlBridge.Dom.Features.SubDocumentBinding module (HtmlBridge complexity-reduction
    // roadmap Phase 3, P3.13). What remains here are two neutral sub-tree search helpers that are
    // shared by both that module and non-frame bridge code — FindInSubTree by the main document's
    // getElementById (Registration.cs) and the module's query callbacks; FindInTree by LayoutMetrics
    // (fragment/id lookup) and document.write — so they stay bridge-owned internal statics.

    /// <summary>Finds the first element in a sub-tree matching a predicate (excludes the root; skips
    /// text nodes and sentinel <c>#</c>-tag elements).</summary>
    internal static DomElement? FindInSubTree(DomNode root, Func<DomElement, bool> predicate)
    {
        foreach (var child in ChildElements(root))
        {
            if (!IsText(child) && !child.TagName.StartsWith("#") && predicate(child))
                return child;
            var found = FindInSubTree(child, predicate);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>Finds the first element in a tree matching a predicate (includes the root).</summary>
    internal static DomElement? FindInTree(DomElement root, Func<DomElement, bool> predicate)
    {
        if (predicate(root)) return root;
        foreach (var child in ChildElements(root))
        {
            var found = FindInTree(child, predicate);
            if (found != null) return found;
        }
        return null;
    }
}
