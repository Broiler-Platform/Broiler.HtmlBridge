using Broiler.Dom.Html;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    internal const int MaxSerializationDepth = 100_000;
    internal const double ZoomSerializationEpsilon = 0.0001;
    internal const double DefaultProgressLikeTrackLengthPx = 120;

    /// <summary>
    /// Serialization of a document with no element child: the doctype alone, matching the
    /// blank reference these tests compare against. The doctype is unconditional here for
    /// the same reason <see cref="HtmlSerializationOptions.IncludeHtmlDoctype"/> is set on
    /// the normal path.
    /// </summary>
    internal const string EmptyDocumentHtml = "<!DOCTYPE html>\n";

    /// <summary>
    /// Whether the element-<c>zoom</c> serialization bake (<see cref="DomBridge.ApplyZoomSerializationStyles"/>)
    /// runs. The bake and the engine used-value model (<c>Broiler.Layout.Engine.NativeZoom</c>,
    /// increments 1–5) are mutually exclusive: running both double-counts <c>zoom</c>, running neither
    /// drops it. The engine flag is <c>[ThreadStatic]</c> and set on the layout thread; the bake mutates
    /// the DOM thread-independently. So the bake is skipped exactly when the engine model is enabled on
    /// this thread — the increment-6 cutover switch. Default (flag off) the bake runs, byte-identical to
    /// before this gate.
    /// </summary>
    internal static bool ZoomBakeActive => !Broiler.Layout.Engine.NativeZoom.Enabled;

    /// <summary>
    /// The <c>content</c> of the first <c>&lt;meta name="color-scheme"&gt;</c> in tree order whose
    /// <c>content</c> is a valid CSS <c>&lt;'color-scheme'&gt;</c> value, or <c>null</c> when there
    /// is none.
    /// <para>
    /// A meta contributes when its <c>name</c> is <c>color-scheme</c>, even if it also carries an
    /// <c>http-equiv</c> attribute (WPT <c>http-equiv-and-name-1</c>): the name metadata is still
    /// processed. A meta whose <c>content</c> is absent, empty, or not a valid color-scheme value
    /// (e.g. the comma-separated <c>light,dark</c>) is skipped, so selection continues to the first
    /// meta that <em>does</em> parse — "first valid applies" (WPT
    /// <c>meta-color-scheme-first-valid-applies</c>).
    /// </para>
    /// </summary>
    internal static string? FindMetaColorScheme(DomElement root)
    {
        foreach (var element in root.Descendants().OfType<DomElement>())
        {
            if (!element.TagName.Equals("meta", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!TryGetAttribute(element, "name", out var name) ||
                !name.Trim().Equals("color-scheme", StringComparison.OrdinalIgnoreCase))
                continue;
            // A meta in a shadow tree is not in the document tree (HTML §4.2.5.3), so it
            // contributes no document-level color scheme (WPT
            // meta-color-scheme-single-value-in-shadow-tree).
            if (FindContainingShadowRoot(element) != null)
                continue;
            if (TryGetAttribute(element, "content", out var content) &&
                IsValidColorSchemeValue(content))
            {
                return content.Trim();
            }
        }
        return null;
    }

    /// <summary>
    /// Whether <paramref name="content"/> is a valid CSS <c>&lt;'color-scheme'&gt;</c> value:
    /// a whitespace-separated list of CSS identifiers (<c>normal</c>, <c>light</c>, <c>dark</c>,
    /// <c>only</c>, or a custom ident). Any other character — most notably a comma, as in the
    /// invalid <c>light,dark</c> — makes the value unparseable, so the meta is ignored per CSS
    /// Color Adjust §2.
    /// </summary>
    private static bool IsValidColorSchemeValue(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var tokens = content.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
            return false;

        foreach (var token in tokens)
            foreach (var ch in token)
                if (!(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' || ch > 0x7F))
                    return false;

        return true;
    }

    /// <summary>
    /// Drops comment nodes from the render-bound document. Comments never render,
    /// but the shared serializer emits them as <c>&lt;!--…--&gt;</c>, which splits
    /// an otherwise-contiguous run of text around a comment (e.g.
    /// <c>"\n&lt;!-- c --&gt;\n"</c> between block siblings) into two separate text
    /// nodes when the canonical HTML is re-parsed for layout. CSS white-space
    /// processing then collapses each run independently, yielding a spurious extra
    /// space between elements (and an uncollapsed leading space at the start of a
    /// block) that shifts all following content — a common cause of the WPT
    /// "MissingContent" pixel mismatches in comment-heavy tests. Removing the
    /// comment nodes lets the surrounding text re-parse as a single node so the run
    /// collapses as the spec requires. Runs only inside <see cref="DomBridge.ApplySerializationTransforms"/>,
    /// so JS-visible <c>innerHTML</c>/<c>outerHTML</c> (which serialize without it)
    /// still expose the comments.
    /// </summary>
    internal static void RemoveRenderCommentNodes(DomElement element)
    {
        for (int i = element.ChildNodes.Count - 1; i >= 0; i--)
        {
            var child = element.ChildNodes[i];
            if (IsComment(child))
            {
                RemoveNthChild(element, i);
                continue;
            }

            if (child is DomElement childElement)
                RemoveRenderCommentNodes(childElement);
        }
    }

    internal static DomElement? FindFirstElementByTagName(DomElement root, string tagName)
    {
        if (string.Equals(root.TagName, tagName, StringComparison.OrdinalIgnoreCase))
            return root;

        foreach (var child in ChildElements(root))
        {
            var match = FindFirstElementByTagName(child, tagName);
            if (match != null)
                return match;
        }

        return null;
    }

    internal static double ReadPixelLength(string? rawValue, double fallback)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return fallback;

        var trimmed = rawValue.Trim();
        if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^2];

        return double.TryParse(
            trimmed,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed)
            ? parsed
            : fallback;
    }

    internal static bool ShouldApplySvgSerializationAttributes(DomElement element)
    {
        var tag = element.TagName.ToLowerInvariant();
        return tag is "svg" or "defs" or "path" or "rect" or "line" or "text" or "textpath" or "polygon" or "polyline";
    }

    internal static readonly HashSet<string> ZoomPreferSpecifiedProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "width",
        "height",
        "min-width",
        "min-height",
        "max-width",
        "max-height"
    };

    internal static readonly string[] ZoomScaledSerializationProperties =
    [
        "width", "height", "min-width", "min-height", "max-width", "max-height",
        "top", "right", "bottom", "left",
        "margin-top", "margin-right", "margin-bottom", "margin-left",
        "padding-top", "padding-right", "padding-bottom", "padding-left",
        "scroll-margin-top", "scroll-margin-right", "scroll-margin-bottom", "scroll-margin-left",
        "scroll-padding-top", "scroll-padding-right", "scroll-padding-bottom", "scroll-padding-left",
        "border-top-width", "border-right-width", "border-bottom-width", "border-left-width",
        "stroke-width",
        "font-size", "line-height", "letter-spacing", "word-spacing", "text-indent",
        "border-radius", "border-top-left-radius", "border-top-right-radius", "border-bottom-right-radius", "border-bottom-left-radius",
        "outline-width", "outline-offset",
        "column-width", "column-height", "column-gap"
    ];

    /// <summary>Whether <paramref name="node"/>'s parent is an HTML raw-text element whose text
    /// content is serialized literally (not HTML-escaped). The standard raw-text element set is
    /// owned by <see cref="HtmlSerializer.RawTextElements"/> (§13.3); this bridge predicate
    /// only applies it to the node's parent. (RF-BRIDGE-1c Phase F, F3c part 2d.)</summary>
    internal static bool IsRawTextSerializationParent(DomNode node) =>
        node.ParentNode is DomElement parent &&
        HtmlSerializer.IsRawTextElement(parent.TagName);
}
