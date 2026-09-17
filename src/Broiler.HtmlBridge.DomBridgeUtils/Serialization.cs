using System.Text.RegularExpressions;
using Broiler.Dom;
using Broiler.Dom.Html;

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
    /// collapses as the spec requires. Runs only inside <c>DomBridge.ApplySerializationTransforms</c>,
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

public static partial class DomBridgeUtils
{
    internal static string ScaleSvgNumericMatch(Match match, double factor)
    {
        if (!double.TryParse(match.Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return match.Value;
        }

        return (number * factor).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static double GetSvgFontRelativeUnitRatio(string unit) => unit.ToLowerInvariant() switch
    {
        // Broiler's SVG length resolution currently uses the same deterministic
        // Ahem-like 0.8em approximation that the existing font-relative zoom
        // coverage already assumes for ex/cap units.
        "ex" or "rex" or "cap" or "rcap" => 0.8,
        _ => 1.0
    };

    internal static readonly string[] SvgZoomScaledUnits =
    [
        "rcap", "rch", "ric", "rex", "rlh", "rem",
        "vmin", "vmax",
        "cap",
        "em", "ex", "ch", "ic", "lh",
        "vw", "vh",
        "px", "pt", "pc", "cm", "mm", "in", "q"
    ];

    internal static readonly HashSet<string> SvgAbsoluteOrViewportUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "vw", "vh", "vmin", "vmax",
        "px", "pt", "pc", "cm", "mm", "in", "q"
    };

    internal static readonly HashSet<string> SvgFontRelativeUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "em", "ex", "cap", "ch", "ic", "lh"
    };

    internal static readonly HashSet<string> SvgRootFontRelativeUnits = new(StringComparer.OrdinalIgnoreCase)
    {
        "rem", "rex", "rcap", "rch", "ric", "rlh"
    };

    [GeneratedRegex(@"-?\d*\.?\d+(?:[eE][+-]?\d+)?")]
    internal static partial System.Text.RegularExpressions.Regex ScaleSvgPointRegex();

    [GeneratedRegex(@"-?\d*\.?\d+(?:[eE][+-]?\d+)?")]
    internal static partial System.Text.RegularExpressions.Regex ScaleSvgPathRegex();

    [GeneratedRegex(@"(?<![\w.-])(-?\d*\.?\d+)px(?:\s*/|(?=\s|$))", RegexOptions.IgnoreCase)]
    internal static partial System.Text.RegularExpressions.Regex FontShortHandRegex();
}

public static partial class DomBridgeUtils
{
    /// <summary>The render-time carrier for a <c>src</c> frame's live document — the counterpart of
    /// <c>srcdoc</c>, read by <c>FragmentTreeBuilder.TryLoadEmbeddedDocument</c>.</summary>
    internal const string FrameDocumentAttr = "data-broiler-frame-document";

    /// <summary>The URL the frame's document was loaded from, so relative references inside it
    /// resolve against the resource rather than against the containing page.</summary>
    internal const string FrameDocumentBaseAttr = "data-broiler-frame-base";

    /// <summary>Whether the resource's markup selects standards mode: its first token that is neither a
    /// comment nor ASCII whitespace is a DOCTYPE named <c>html</c>.</summary>
    /// <remarks>
    /// <para>
    /// That is HTML §13.2.6.4.1's "initial" insertion mode: comments and ASCII whitespace may come
    /// before the DOCTYPE, any other token ends the mode in quirks, and a DOCTYPE after that is a parse
    /// error that changes nothing. A DOCTYPE with any other name is quirks too, which is the rule the
    /// page's own serialization applies (<c>DomBridge.SelectsStandardsMode</c>). A <c>&lt;?xml?&gt;</c>
    /// prolog or a <c>&lt;!x&gt;</c> is a bogus comment and does not end the mode.
    /// </para>
    /// <para>
    /// It reads tokens rather than the parsed tree because the tree cannot answer it: the shared parser
    /// inserts the first DOCTYPE token before <c>&lt;html&gt;</c> wherever the token appears, and has no
    /// document-mode output. The tokenizer is lazy, so this stops at the first token that decides.
    /// </para>
    /// <para>
    /// A frame nobody scripted is not stamped, and its mode comes from Layout's
    /// <c>DocumentModeContext.IsQuirksHtml</c> instead, which takes the first <c>&lt;!doctype</c> anywhere in
    /// the source. So the two disagree for a frame with content before its DOCTYPE: it renders in
    /// standards mode until a script touches it and in quirks mode after, which is Chromium's answer. The
    /// hand scanner disagreed the same way, except for a leading non-ASCII space such as U+00A0, which it
    /// skipped as whitespace and this does not.
    /// </para>
    /// </remarks>
    internal static bool HasHtmlDoctype(string html)
    {
        foreach (var token in new HtmlTokenizer().Tokenize(html))
        {
            if (token.Type == TokenType.Comment ||
                (token.Type == TokenType.Character && token.Data.AsSpan().Trim(AsciiWhitespace).IsEmpty))
                continue;

            return token.Type == TokenType.Doctype && string.Equals(token.Name, "html", StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>The ASCII whitespace the HTML Standard ignores between tokens: tab, LF, FF, CR and
    /// space.</summary>
    private const string AsciiWhitespace = "\t\n\f\r ";
}
