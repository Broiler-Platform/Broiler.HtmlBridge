using System.Globalization;
using System.Text.RegularExpressions;
using Broiler.CSS;
using Broiler.CSS.Dom;
using Broiler.Dom;
using Broiler.HtmlBridge.Internal.Scripting;

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
        while (ParentEl(root) is { } parent)
        {
            // If we've reached a scope root (shadow root), stop here
            if (root.TagName.StartsWith('#'))
                return root;
            root = parent;
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
    /// ("Destination array was not long enough"). Either previously aborted style
    /// collection for the whole tree, leaving the document unstyled; both are still
    /// caught.
    /// Retry a bounded number of times, then fall back to a tolerant index walk.
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

    internal static void ApplyUserAgentPropertyDefaults(Dictionary<string, string> computed, DomElement element)
    {
        if (CssUserAgentDefaults.PropertyValues.TryGetValue(element.TagName, out var props))
        {
            foreach (var (prop, val) in props)
            {
                if (!computed.ContainsKey(prop))
                    computed[prop] = val;
            }
        }
    }

    /// <summary>
    /// Expands CSS shorthand properties into individual longhand properties (e.g.
    /// <c>margin: 10px 5px</c> → <c>margin-top/right/bottom/left</c>), only setting longhands
    /// not already present. Delegates to the single canonical
    /// <see cref="CssStyleEngine.ExpandShorthands"/>, so the expansion cannot drift from the engine's.
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
    /// The pixel length <paramref name="property"/> has in an inline declaration map as
    /// <see cref="ParseStyle"/> builds it, or 0 when the property is absent or its value is not a
    /// length.
    /// </summary>
    /// <remarks>
    /// Searching the raw <c>style</c> text for the first occurrence of the name and reading up to the
    /// next <c>;</c> would find <c>width</c> inside <c>max-width</c> and <c>border-width</c> and
    /// <c>height</c> inside <c>line-height</c>, read declarations out of comments, take the first of
    /// two declarations where CSS takes the last, and read <c>450px !important</c> as no length at
    /// all. The map is the one the element's own inline style
    /// is built from, so the declarations are parsed and validated exactly as that style's are, and a
    /// declaration the renderer drops is not read here either. <see cref="ParseStyle"/> keeps
    /// <c> !important</c> on the value, hence the strip.
    /// <para>
    /// It is still one declaration, not the used size, so a frame's viewport can differ from the box the
    /// renderer draws: <c>min-</c>/<c>max-width</c> and <c>-height</c> clamps are not applied,
    /// <c>box-sizing</c>, padding and border are not taken off to reach the content box, and an author
    /// <c>!important</c> rule that overrides the inline value is not consulted.
    /// </para>
    /// </remarks>
    internal static int ExtractCssDimension(IReadOnlyDictionary<string, string> declarations, string property)
    {
        if (!declarations.TryGetValue(property, out var value))
            return 0;

        var px = ParseCssLengthToPixels(CssPriority.Strip(value));
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

public static partial class DomBridgeUtils
{
    /// <summary>
    /// Returns <c>true</c> if the element is a <c>&lt;link rel="stylesheet" href="..."&gt;</c>.
    /// </summary>
    internal static bool IsExternalStylesheet(DomElement element)
    {
        if (!string.Equals(element.TagName, "link", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!TryGetAttribute(element, "rel", out var rel) ||
            !rel.Contains("stylesheet", StringComparison.OrdinalIgnoreCase))
            return false;
        return HasAttr(element, "href");
    }

    /// <summary>
    /// Whether <paramref name="element"/> owns a style sheet the cascade reads: a <c>&lt;style&gt;</c>, or a
    /// <c>&lt;link rel="stylesheet" href="..."&gt;</c>. The one test behind both the scope's sheet walk and the
    /// DOM edits that make computed style stale (<c>DomBridge.OnStyleSheetSourceMutation</c>), so the two
    /// cannot disagree about what a sheet is.
    /// </summary>
    internal static bool IsStyleSheetOwner(DomElement element) =>
        string.Equals(element.TagName, "style", StringComparison.OrdinalIgnoreCase) || IsExternalStylesheet(element);
}

public static partial class DomBridgeUtils
{
    /// <summary>Whether a <c>&lt;link&gt;</c>'s space-separated <c>rel</c> token list includes
    /// <c>stylesheet</c> (case-insensitive), so only sheet links have their href re-based.</summary>
    internal static bool LinkRelIsStyleSheet(DomElement element)
    {
        if (!TryGetAttribute(element, "rel", out var rel) || string.IsNullOrWhiteSpace(rel))
            return false;

        foreach (var token in rel.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (token.Equals("stylesheet", StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }
}

public static partial class DomBridgeUtils
{
    /// <summary>Depth cap for nested <c>@import</c> chains — a backstop against a
    /// pathological chain; well above any real stylesheet's nesting.</summary>
    internal const int MaxImportDepth = 32;

    internal static readonly System.Text.RegularExpressions.Regex UrlFunctionPattern = new(
        @"url\(\s*(['""]?)([^'""\)]+)\1\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Rewrites relative <c>url(...)</c> references in imported CSS to absolute URLs against
    /// the imported sheet's own URL, so an imported sheet's relative images resolve against
    /// the sheet rather than the importing document. Absolute, <c>data:</c>, and fragment
    /// references are left untouched; when the base is itself a <c>data:</c> URL (no path to
    /// resolve against) the text is returned unchanged.
    /// </summary>
    internal static string RebaseRelativeUrls(string css, string baseUrl)
    {
        if (baseUrl.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return css;

        return UrlFunctionPattern.Replace(css, match =>
        {
            var raw = match.Groups[2].Value.Trim();
            if (raw.Length == 0 ||
                raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("#", StringComparison.Ordinal) ||
                HasUrlScheme(raw))
                return match.Value;

            var resolved = UrlResolver.Resolve(raw, baseUrl)?.AbsoluteUri;
            return resolved is null ? match.Value : $"url(\"{resolved}\")";
        });
    }

    /// <summary>Whether a URL reference already carries an explicit scheme
    /// (<c>scheme:</c> or protocol-relative <c>//host</c>), so it should not be re-based.</summary>
    private static bool HasUrlScheme(string url)
    {
        if (url.StartsWith("//", StringComparison.Ordinal))
            return true;
        var colon = url.IndexOf(':');
        if (colon <= 0)
            return false;
        for (var i = 0; i < colon; i++)
        {
            var c = url[i];
            if (!char.IsLetterOrDigit(c) && c != '+' && c != '-' && c != '.')
                return false;
        }
        return true;
    }
}
