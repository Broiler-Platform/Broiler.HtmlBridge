using System.Collections.Generic;
using System.Globalization;
using Broiler.Dom;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

/// <summary>
/// The order a document's elements are painted in, which is the order a point hits them in: CSS 2.1
/// Appendix E, and HTML's top layer above it all.
/// </summary>
/// <remarks>
/// <para>
/// <b>The hit test took the last element in tree order</b> that covered the point, which is the painted
/// one only while nothing is positioned, floated or stacked. A positioned box early in the document lies
/// over the in-flow blocks after it, a <c>z-index: -1</c> box lies under its parent's background, the
/// earlier of two positioned siblings lies over the later one when its <c>z-index</c> is higher, and a
/// modal dialog lies over everything -- so a press on the box the user saw went to another (measured in
/// Chromium).
/// </para>
/// <para>
/// <b>Within a stacking context</b> the painting goes: the context's own box; its stacking contexts with a
/// negative <c>z-index</c>; its in-flow blocks; its floats; its inline content; its positioned descendants
/// with <c>z-index: auto</c> or <c>0</c> and its stacking contexts with <c>z-index: 0</c>, in tree order; and
/// its stacking contexts with a positive <c>z-index</c>, lowest first. A float, an inline-level atomic box
/// and a positioned box with <c>z-index: auto</c> are painted as a unit, as though they made a stacking
/// context, except that a positioned or stacking-context descendant of theirs belongs to the stacking
/// context around them.
/// </para>
/// <para>
/// <b>Only the order is decided here.</b> Which elements cover the point is still the hit test's own
/// question (<c>HitTesting.cs</c>), with its rounded corners, image maps, list markers and SVG shapes.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    /// <summary>The elements of the tree under <paramref name="documentElement"/>, each with its place in the painting order: higher paints later, on top.</summary>
    private Dictionary<DomElement, int> PaintRanks(DomElement documentElement)
    {
        var walk = new PaintWalk(this);
        walk.TreeOrder(documentElement);
        walk.PaintStackingContext(documentElement);
        foreach (var element in TopLayerOf(GetOwningDocument(documentElement)))
            walk.PaintStackingContext(element);

        return walk.Ranks;
    }

    /// <summary>
    /// <paramref name="hits"/>, the elements of <paramref name="root"/>'s tree that cover a point, topmost first
    /// in the painting order. While a modal dialog is open the rest of the document is inert, under the
    /// dialog's backdrop: a point hits the dialog, what of it covers the point, and the root (measured).
    /// </summary>
    private List<DomElement> InPaintOrder(DomElement root, List<DomElement> hits)
    {
        if (BlockingModalDialog(GetOwningDocument(root)) is { } dialog)
        {
            hits = hits.FindAll(hit => ReferenceEquals(hit, root) || IsInclusiveAncestorOf(dialog, hit));
            if (!hits.Contains(dialog))
                hits.Add(dialog);
        }

        if (hits.Count < 2)
            return hits;

        var ranks = PaintRanks(root);
        var order = new List<(DomElement Element, int Rank, int Index)>(hits.Count);
        for (var i = 0; i < hits.Count; i++)
            order.Add((hits[i], ranks.TryGetValue(hits[i], out var rank) ? rank : -1, i));

        order.Sort(static (a, b) => a.Rank != b.Rank ? b.Rank.CompareTo(a.Rank) : a.Index.CompareTo(b.Index));
        return order.ConvertAll(static entry => entry.Element);
    }

    private static bool IsInclusiveAncestorOf(DomElement ancestor, DomElement element)
    {
        for (var current = element; current is not null; current = ParentEl(current))
        {
            if (ReferenceEquals(current, ancestor))
                return true;
        }

        return false;
    }

    /// <summary>The elements of <paramref name="document"/> in the top layer, in the order they went into it.</summary>
    private List<DomElement> TopLayerOf(DomDocument document)
    {
        var layer = new List<DomElement>();
        foreach (var element in document.Descendants().OfType<DomElement>())
        {
            if (IsInTopLayer(element))
                layer.Add(element);
        }

        layer.Sort((a, b) => DialogStateFor(a).TopLayerOrder.Value.CompareTo(DialogStateFor(b).TopLayerOrder.Value));
        return layer;
    }

    /// <summary>Whether <paramref name="element"/> is in the top layer: a modal dialog, a showing popover, a fullscreen element.</summary>
    private bool IsInTopLayer(DomElement element)
    {
        var state = DialogStateFor(element);
        return IsModalDialog(element) ||
               state.PopoverOpen is { IsSet: true, Value: true } ||
               state.Fullscreen is { IsSet: true, Value: true };
    }

    /// <summary>The modal dialog of <paramref name="document"/> last put in the top layer -- the one that makes the rest of the document inert -- or null.</summary>
    private DomElement? BlockingModalDialog(DomDocument document)
    {
        DomElement? blocking = null;
        foreach (var element in document.Descendants().OfType<DomElement>())
        {
            if (IsModalDialog(element) &&
                (blocking is null || DialogStateFor(element).TopLayerOrder.Value > DialogStateFor(blocking).TopLayerOrder.Value))
            {
                blocking = element;
            }
        }

        return blocking;
    }

    private sealed class PaintWalk(DomBridge bridge)
    {
        private readonly Dictionary<DomElement, int> _treeOrder = new(ReferenceEqualityComparer.Instance);

        public Dictionary<DomElement, int> Ranks { get; } = new(ReferenceEqualityComparer.Instance);

        /// <summary>Numbers the elements under <paramref name="root"/> in tree order, which breaks every tie in the painting order.</summary>
        public void TreeOrder(DomElement root)
        {
            _treeOrder[root] = _treeOrder.Count;
            foreach (var child in root.ChildNodes)
            {
                if (child is DomElement element && !element.TagName.StartsWith('#'))
                    TreeOrder(element);
            }
        }

        /// <summary>Paints <paramref name="root"/>, a stacking context, and everything it contains.</summary>
        public void PaintStackingContext(DomElement root)
        {
            var group = new Group(root, isStackingContext: true);
            Classify(root, group, group);

            Emit(root);
            foreach (var (_, element) in Ordered(group.Contexts, z => z < 0))
                PaintContextOrUnit(element);
            PaintBody(group);
            foreach (var (_, element) in Ordered(group.Contexts, z => z == 0))
                PaintContextOrUnit(element);
            foreach (var (_, element) in Ordered(group.Contexts, z => z > 0))
                PaintContextOrUnit(element);
        }

        private void PaintContextOrUnit(DomElement element)
        {
            if (_units.TryGetValue(element, out var unit))
                PaintUnit(unit);
            else
                PaintStackingContext(element);
        }

        // The floats, inline-level atomic boxes and positioned z-index:auto boxes, each with what it paints as a unit.
        private readonly Dictionary<DomElement, Group> _units = new(ReferenceEqualityComparer.Instance);

        private void PaintUnit(Group unit)
        {
            Emit(unit.Root);
            PaintBody(unit);
        }

        private void PaintBody(Group group)
        {
            foreach (var block in group.Blocks)
                Emit(block);
            foreach (var floated in group.Floats)
                PaintUnit(_units[floated]);
            foreach (var inline in group.Inlines)
            {
                if (_units.TryGetValue(inline, out var atomic))
                    PaintUnit(atomic);
                else
                    Emit(inline);
            }
        }

        private void Emit(DomElement element) => Ranks[element] = Ranks.Count;

        private IEnumerable<(int Z, DomElement Element)> Ordered(List<(int Z, DomElement Element)> contexts, Func<int, bool> which)
        {
            var chosen = new List<(int Z, DomElement Element)>();
            foreach (var context in contexts)
            {
                if (which(context.Z))
                    chosen.Add(context);
            }

            chosen.Sort((a, b) => a.Z != b.Z ? a.Z.CompareTo(b.Z) : TreeIndex(a.Element).CompareTo(TreeIndex(b.Element)));
            return chosen;
        }

        private int TreeIndex(DomElement element) => _treeOrder.TryGetValue(element, out var index) ? index : int.MaxValue;

        /// <summary>
        /// Sorts <paramref name="parent"/>'s children into <paramref name="group"/>, what paints with it, and into
        /// <paramref name="context"/>, the stacking context the group belongs to.
        /// </summary>
        private void Classify(DomElement parent, Group group, Group context)
        {
            foreach (var child in parent.ChildNodes)
            {
                if (child is not DomElement element || element.TagName.StartsWith('#'))
                    continue;

                var props = bridge.GetComputedProps(element);
                var display = Value(props, "display");
                if (display == "none" || bridge.IsInTopLayer(element))
                    continue;

                if (display == "contents")
                {
                    Classify(element, group, context);
                    continue;
                }

                var position = Value(props, "position");
                var positioned = position is not ("" or "static");
                var zIndex = ZIndex(props, positioned || IsFlexOrGridItem(parent));
                if (CreatesStackingContext(element, props, position, zIndex))
                {
                    context.Contexts.Add((zIndex ?? 0, element));
                    continue;
                }

                if (positioned)
                {
                    context.Contexts.Add((0, element));
                    Unit(element, context);
                    continue;
                }

                if (Value(props, "float") is "left" or "right" or "inline-start" or "inline-end")
                {
                    group.Floats.Add(element);
                    Unit(element, context);
                    continue;
                }

                if (IsAtomicInline(element, display))
                {
                    group.Inlines.Add(element);
                    Unit(element, context);
                    continue;
                }

                if (display.StartsWith("inline", StringComparison.Ordinal) || display is "ruby" or "ruby-text")
                    group.Inlines.Add(element);
                else
                    group.Blocks.Add(element);

                Classify(element, group, context);
            }
        }

        private void Unit(DomElement element, Group context)
        {
            var unit = new Group(element, isStackingContext: false);
            _units[element] = unit;
            Classify(element, unit, context);
        }

        private bool IsFlexOrGridItem(DomElement parent) =>
            Value(bridge.GetComputedProps(parent), "display") is "flex" or "inline-flex" or "grid" or "inline-grid";

        private static int? ZIndex(IReadOnlyDictionary<string, string> props, bool applies) =>
            applies && int.TryParse(Value(props, "z-index"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var z) ? z : null;

        private static bool CreatesStackingContext(DomElement element, IReadOnlyDictionary<string, string> props, string position, int? zIndex)
        {
            if (zIndex is not null || position is "fixed" or "sticky")
                return true;

            if (double.TryParse(Value(props, "opacity"), NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity) && opacity < 1)
                return true;

            foreach (var property in (ReadOnlySpan<string>)["transform", "filter", "backdrop-filter", "perspective", "clip-path", "mask", "mask-image"])
            {
                if (Value(props, property) is not ("" or "none"))
                    return true;
            }

            if (Value(props, "mix-blend-mode") is not ("" or "normal") || Value(props, "isolation") == "isolate")
                return true;

            var contain = Value(props, "contain");
            if (contain.Contains("paint", StringComparison.Ordinal) || contain.Contains("layout", StringComparison.Ordinal) ||
                contain is "strict" or "content")
            {
                return true;
            }

            var willChange = Value(props, "will-change");
            return willChange.Contains("opacity", StringComparison.Ordinal) || willChange.Contains("transform", StringComparison.Ordinal) ||
                   willChange.Contains("filter", StringComparison.Ordinal);
        }

        private static bool IsAtomicInline(DomElement element, string display) =>
            display is "inline-block" or "inline-flex" or "inline-grid" or "inline-table" ||
            display == "inline" && element.TagName.ToLowerInvariant() is
                "img" or "video" or "canvas" or "iframe" or "embed" or "object" or "input" or "button" or "select" or "textarea" or "svg" or "audio" or "meter" or "progress";

        private static string Value(IReadOnlyDictionary<string, string> props, string name) =>
            props.TryGetValue(name, out var value) ? value.Trim().ToLowerInvariant() : string.Empty;

        private sealed class Group(DomElement root, bool isStackingContext)
        {
            public DomElement Root { get; } = root;

            public bool IsStackingContext { get; } = isStackingContext;

            // Only a stacking context's: the stacking contexts and positioned boxes it contains, with their z-index.
            public List<(int Z, DomElement Element)> Contexts { get; } = [];

            public List<DomElement> Blocks { get; } = [];

            public List<DomElement> Floats { get; } = [];

            public List<DomElement> Inlines { get; } = [];
        }
    }
}
