using System.Globalization;
using Broiler.Dom;
using Broiler.CSS;
using Broiler.CSS.Dom;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    /// <summary>
    /// Finds the style-scope root ancestor for the given element by walking up the
    /// parent chain. Stops at a <c>#</c>-prefixed boundary element (a <c>#shadow-root</c>;
    /// the document/sub-document sentinels are gone — the canonical <c>DomDocument</c> parent
    /// is not a <c>DomElement</c>, so <see cref="ParentEl"/> already stops the walk there).
    /// Returns the topmost element within the element's scope.
    /// </summary>
    internal static DomElement GetDocumentRootFor(DomElement el)
    {
        var root = el;
        while (ParentEl(root) != null)
        {
            // If we've reached a scope root (shadow root), stop here
            if (root.TagName.StartsWith('#'))
                return root;
            root = ParentEl(root);
        }
        return root;
    }

    /// <summary>
    /// Snapshots an element's children in a way that tolerates concurrent DOM
    /// mutation (parallel WPT rendering, JS-driven tree edits, or a lazy
    /// sub-document root materialising during the walk).
    /// </summary>
    /// <remarks>
    /// A plain <c>ChildElements(root).ToList()</c> is NOT thread-safe here.
    /// <see cref="ChildElements"/> is a lazy <c>OfType</c> filter over the live
    /// <see cref="DomNode.ChildNodes"/> list, so <see cref="Enumerable.ToList{T}"/>
    /// enumerates that list, and a mutation during the walk throws
    /// <see cref="InvalidOperationException"/> ("Collection was modified"). The
    /// facade's <c>LegacyChildList</c> projection, since removed, failed a second
    /// way as well: its <c>Count</c>-sized <c>CopyTo</c> overflowed when another
    /// thread appended in between, throwing <see cref="ArgumentException"/>
    /// ("Destination array was not long enough" — signature
    /// <c>DomBridge.CollectStyleElementsInTree</c>). Either previously aborted
    /// style collection for the whole tree, leaving the document unstyled; both
    /// are still caught. Retry a bounded number of times, then fall back to a
    /// tolerant index walk.
    /// </remarks>
    internal static List<DomElement> SnapshotChildren(DomElement root)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                return ChildElements(root).ToList();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                // Concurrent structural mutation raced the snapshot; retry with a
                // fresh copy. Transient contention almost always clears in a
                // couple of attempts.
            }
        }

        // Sustained contention: copy element-by-element, re-checking bounds each
        // step so a shrinking list can only truncate the snapshot, never throw.
        var snapshot = new List<DomElement>();
        for (var i = 0; ; i++)
        {
            DomElement? child;
            try
            {
                if (i >= root.ChildNodes.Count)
                    break;
                // Element snapshot: a char-data child (post-flip) is skipped (null).
                child = ChildAt(root, i) as DomElement;
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or InvalidOperationException)
            {
                break;
            }

            if (child is not null)
                snapshot.Add(child);
        }

        return snapshot;
    }

    internal static bool IsSelectListBox(DomElement element) => GetSelectVisibleRowCount(element) > 1;

    private static int GetSelectVisibleRowCount(DomElement element)
    {
        bool isMultiple = HasAttr(element, "multiple");
        if (TryGetAttribute(element, "size", out var rawSize) &&
            int.TryParse(rawSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedSize) &&
            parsedSize > 0)
        {
            return parsedSize;
        }

        return isMultiple ? 4 : 1;
    }

    internal static void ApplyUserAgentDisplayDefaults(Dictionary<string, string> computed, DomElement element)
    {
        if (computed.ContainsKey("display"))
            return;

        if (HasAttr(element, "hidden"))
        {
            computed["display"] = "none";
            return;
        }

        if (CssUserAgentDefaults.DisplayValues.TryGetValue(element.TagName, out var display))
            computed["display"] = display;
    }

    /// <summary>
    /// Expands CSS shorthand properties into individual longhand properties (e.g.
    /// <c>margin: 10px 5px</c> → <c>margin-top/right/bottom/left</c>), only setting longhands
    /// not already present. DOM/CSS promotion Phase 2: this now delegates to the single canonical
    /// <see cref="CssStyleEngine.ExpandShorthands"/> — the bridge's own copy (which
    /// had drifted to a narrower subset: no <c>outline</c>, no <c>font</c> slash line-height, and a
    /// single-layer <c>background</c> parser) is deleted so it can no longer drift from the engine.
    /// </summary>
    internal static void ExpandCssShorthands(Dictionary<string, string> computed)
        => CssStyleEngine.ExpandShorthands(computed);

    /// <summary>
    /// Parses a CSS length value (e.g. "0", "100px", "1em") to pixels, returning
    /// <see cref="double.NaN"/> when it cannot be parsed. Font-free approximation
    /// (1em = 16px default); the algorithm is owned by the canonical
    /// <see cref="CssLengthParser.ParseToPixels"/>.
    /// </summary>
    internal static double ParseCssLengthToPixels(string value, int viewportWidth = 0, int viewportHeight = 0) =>
        CssLengthParser.ParseToPixels(value, viewportWidth, viewportHeight);

    /// <summary>
    /// Extracts a pixel dimension from a CSS style string for a given property name.
    /// </summary>
    internal static int ExtractCssDimension(string style, string property)
    {
        var propIdx = style.IndexOf(property, StringComparison.OrdinalIgnoreCase);
        if (propIdx < 0) return 0;
        var colonIdx = style.IndexOf(':', propIdx);
        if (colonIdx < 0) return 0;
        var semiIdx = style.IndexOf(';', colonIdx);
        var valueStr = semiIdx >= 0 ? style[(colonIdx + 1)..semiIdx].Trim() : style[(colonIdx + 1)..].Trim();
        var px = ParseCssLengthToPixels(valueStr);
        return !double.IsNaN(px) ? (int)px : 0;
    }

    internal static int ParseViewportDimensionAttribute(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        var px = ParseCssLengthToPixels(value.Trim());
        return !double.IsNaN(px) && px > 0 ? (int)px : 0;
    }
}
