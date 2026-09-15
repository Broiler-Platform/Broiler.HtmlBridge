using Broiler.HtmlBridge.Dom;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

/// <summary>
/// The static members of <c>DomBridge</c> that need no bridge instance: constants, shared state and helpers,
/// in their own assembly below Broiler.HtmlBridge.Dom. The partial files here mirror the <c>DomBridge</c> files
/// they came from. The few helpers that need a bridge, a feature binding or the JavaScript engine are
/// <c>DomBridgeHostUtils</c>, which stays in Dom.
/// </summary>
public static partial class DomBridgeUtils
{
    /// <summary>
    /// Safety cap for draining bridge-backed microtask/timer work so promise/timer
    /// chains can settle without risking an infinite loop in test and capture paths.
    /// </summary>
    public const int AsyncDrainIterationLimit = 1000;

    /// <summary>
    /// How far onto the virtual clock a drain follows scheduled work (ms from document start); see
    /// <see cref="DomBridgeRuntimeLimits.AsyncDrainVirtualTimeBudgetMs"/>.
    /// </summary>
    public const double AsyncDrainVirtualTimeBudgetMs = DomBridgeRuntimeLimits.AsyncDrainVirtualTimeBudgetMs;

    internal static readonly string[] InlineEventNames = ["click", "load", "change", "input", "submit", "mousedown",
        "mouseup", "mouseover", "mouseout", "keydown", "keyup", "keypress", "focus", "blur", "error", "scroll",
        "scrollend"];

    /// <summary>The viewport a bridge assumes when its host does not say otherwise.</summary>
    public const int DefaultViewportWidth = 1024;

    /// <inheritdoc cref="DefaultViewportWidth"/>
    public const int DefaultViewportHeight = 768;

    // -----------------------------------------------------------------
    // RF-BRIDGE-1c Phase E2: child-node access over canonical ChildNodes,
    // replacing the facade Broiler.Dom.DomElement.Children (LegacyChildList, since removed).
    // Phase F has since made text and comment nodes canonical DomText/DomComment children
    // (CreateBridgeTextNode), so ChildElements is an OfType filter that skips them, ChildAt
    // answers a DomNode, and callers that need text or comment children walk ChildNodes
    // with IsText/IsComment checks.
    // -----------------------------------------------------------------

    /// <summary>The element's <see cref="DomElement"/> children. RF-BRIDGE-1c Phase F (F3c part 2c):
    /// narrowed from <c>Cast</c> to <c>OfType&lt;Broiler.Dom.DomElement&gt;()</c> so it skips canonical
    /// <c>DomText</c>/<c>DomComment</c> children, which the bridge creates today (see
    /// <c>DomBridge.CreateBridgeTextNode</c>). Callers that need text/comment children walk raw
    /// <c>ChildNodes</c> instead.</summary>
    internal static IEnumerable<DomElement> ChildElements(DomNode element) =>
        element.ChildNodes.OfType<DomElement>();

    /// <summary>The child node at <paramref name="index"/> (old <c>Children[index]</c>). RF-BRIDGE-1c
    /// Phase F (F3c part 2c): returns canonical <see cref="DomNode"/> — a child may be a
    /// <c>DomText</c>/<c>DomComment</c>. Element-only callers narrow with <c>as Broiler.Dom.DomElement</c>
    /// or <c>is Broiler.Dom.DomElement</c>, since not every child is an element.</summary>
    internal static DomNode ChildAt(DomNode element, int index) => element.ChildNodes[index];

    /// <summary>The child node at <paramref name="index"/>, supporting from-end indices like <c>^1</c>
    /// (old <c>Children[^1]</c>); canonical <c>ChildNodes</c> is an <c>IReadOnlyList</c> with no
    /// from-end indexer.</summary>
    internal static DomNode ChildAt(DomNode element, Index index) =>
        element.ChildNodes[index.GetOffset(element.ChildNodes.Count)];

    /// <summary>Index of <paramref name="child"/> among the element's children, or -1
    /// (old <c>Children.IndexOf</c>, reference equality). Phase 4 item 4/5: canonical
    /// <c>Broiler.Dom.DomNodeCollectionExtensions.IndexOfReference</c> is the byte-identical scan, but the
    /// reference-equality child-index scan is the canonical <c>DomNodeCollectionExtensions.IndexOfReference</c>
    /// (P4.17 reuse), which `patches/0002` made public and which is now pinned — so the former manual loop
    /// delegates to it (byte-identical).</summary>
    internal static int ChildIndexOf(DomNode element, DomNode child) => element.ChildNodes.IndexOfReference(child);

    // RF-BRIDGE-1c Phase F (F3c part 2b): the child-mutation helpers take a DomNode parent so
    // range-extract code (whose ancestor-chain clones are DomNode-typed) can reparent without
    // casts. At runtime the parent is always an element; canonical AppendChild/InsertBefore/
    // RemoveChild enforce nothing text-specific, so this is a safe widen.

    /// <summary>Old <c>Children.Insert(index, child)</c>.</summary>
    internal static void InsertChildAt(DomNode parent, int index, DomNode child)
    {
        var reference = index < parent.ChildNodes.Count ? parent.ChildNodes[index] : null;
        parent.InsertBefore(child, reference);
    }

    /// <summary>Old <c>Children.Remove(child)</c> — removes only if actually a child; returns success.</summary>
    internal static bool RemoveChildFrom(DomNode parent, DomNode child)
    {
        if (!ReferenceEquals(child.ParentNode, parent))
            return false;

        parent.RemoveChild(child);
        return true;
    }

    /// <summary>Old raw <c>Children.RemoveAt(index)</c>, now canonical <c>RemoveChild</c>, which publishes
    /// its own child-list mutation record; <c>DomBridge.RemoveChildAt</c> adds a style-scope invalidation
    /// (its two notify hooks are empty).</summary>
    internal static void RemoveNthChild(DomNode parent, int index) => parent.RemoveChild(parent.ChildNodes[index]);

    /// <summary>Old <c>Children.Clear()</c>.</summary>
    internal static void ClearChildren(DomNode parent)
    {
        foreach (var child in parent.ChildNodes.ToArray())
            parent.RemoveChild(child);
    }

    /// <summary>Whether <paramref name="node"/> is a text node (RF-BRIDGE-1c Phase D: replaces
    /// the facade <c>IsText(Broiler.Dom.DomElement)</c>). NodeType-based; construction has flipped,
    /// so a text node is a canonical <c>DomText</c> (<c>DomBridge.CreateBridgeTextNode</c>). (This said
    /// it held for facade text nodes, and for <c>DomText</c> once construction flipped.)</summary>
    internal static bool IsText(DomNode node) => node.NodeType == DomNodeType.Text;

    /// <summary>Whether <paramref name="node"/> is a comment node (RF-BRIDGE-1c Phase F).
    /// NodeType-based — the replacement for the many <c>TagName == "#comment"</c> checks, since a
    /// canonical <c>DomComment</c> has no <c>TagName</c>; construction has flipped, so every comment
    /// is one (<c>DomBridge.CreateBridgeCommentNode</c>). (This said it held for facade comment nodes,
    /// and for <c>DomComment</c> once construction flipped.)</summary>
    internal static bool IsComment(DomNode node) => node.NodeType == DomNodeType.Comment;

    /// <summary>Reads a text/comment node's character data (RF-BRIDGE-1c Phase F): a canonical
    /// <c>DomText</c>/<c>DomComment</c>'s <c>Data</c>, otherwise <c>NodeValue</c>, which no other node
    /// kind overrides, so <c>""</c> (never null). (This also named facade text/comment nodes that
    /// exposed it as <c>TextContent</c>, and a text cutover both models funnelled through.)</summary>
    internal static string BridgeText(DomNode node) => node switch
    {
        DomCharacterData characterData => characterData.Data,
        _ => node.NodeValue ?? string.Empty,
    };

    /// <summary>Writes a text/comment node's character data (see <see cref="BridgeText"/>).</summary>
    internal static void SetBridgeText(DomNode node, string value)
    {
        if (node is DomCharacterData characterData)
            characterData.Data = value;
    }

    /// <summary>An element's <c>textContent</c> — the concatenation of its descendant text (RF-BRIDGE-1c
    /// Phase F, F3c part 2d). Replaces reads of the former element-store <c>Broiler.Dom.DomElement.TextContent</c>.</summary>
    internal static string GetElementTextContent(DomElement element)
    {
        var sb = new System.Text.StringBuilder();
        CollectTextContent(element, sb);
        return sb.ToString();
    }

    /// <summary>The element's parent as a <see cref="DomElement"/> (RF-BRIDGE-1c Phase E:
    /// replaces the facade <c>ParentEl(Broiler.Dom.DomElement)</c> getter — <c>ParentNode as Broiler.Dom.DomElement</c>).
    /// A node's parent is always an element, so this is stable when text/comment nodes become
    /// canonical <c>DomText</c>/<c>DomComment</c> in Phase D.</summary>
    internal static DomElement? ParentEl(DomNode node) => node.ParentNode as DomElement;

    /// <summary>Reparents <paramref name="child"/> under <paramref name="parent"/> (RF-BRIDGE-1c
    /// Phase E: replaces the facade <c>ParentEl(Broiler.Dom.DomElement)</c> setter). A null parent detaches;
    /// otherwise the child is appended if not already there — matching the old setter exactly.
    /// RF-BRIDGE-1c Phase F (F3c part 2b): the parent widened to <c>DomNode?</c> so range-extract
    /// code can pass DomNode-typed ancestor-chain clones (always elements at runtime).</summary>
    internal static void SetParent(DomNode child, DomNode? parent)
    {
        if (parent is null)
            child.Remove();
        else if (!ReferenceEquals(child.ParentNode, parent))
            parent.AppendChild(child);
    }
}
