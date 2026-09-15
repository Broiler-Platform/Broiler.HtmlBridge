using Broiler.Dom;
using Broiler.Layout;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    /// <summary>
    /// True when the computed <c>position</c> in <paramref name="props"/> is
    /// <c>sticky</c>.
    /// </summary>
    internal static bool IsSticky(Dictionary<string, string> props) =>
        string.Equals(props.GetValueOrDefault("position"), "sticky", StringComparison.OrdinalIgnoreCase);
    /// <summary>
    /// True when <paramref name="node"/> is <paramref name="ancestor"/> or a
    /// descendant of it.
    /// </summary>
    internal static bool IsDescendantOrSelf(DomElement node, DomElement ancestor)
    {
        for (var cur = node; cur != null; cur = ParentEl(cur))
            if (cur == ancestor)
                return true;
        return false;
    }
    internal static bool IsLayoutProperty(string prop) => prop switch
    {
        "position" or "top" or "right" or "bottom" or "left"
            or "margin" or "margin-top" or "margin-right"
            or "margin-bottom" or "margin-left"
            or "width" or "height" => true,
        _ => false,
    };

    /// <summary>
    /// Maps the CSS inset property an <c>anchor()</c> resolves into to the
    /// <see cref="AnchorInsetProperty"/> the Layout edge resolver flips against
    /// (only right/bottom differ; everything else uses the raw edge).
    /// </summary>
    internal static AnchorInsetProperty MapAnchorInsetProperty(string property) => property switch
    {
        "right" => AnchorInsetProperty.Right,
        "bottom" => AnchorInsetProperty.Bottom,
        "left" => AnchorInsetProperty.Left,
        "top" => AnchorInsetProperty.Top,
        _ => AnchorInsetProperty.Other,
    };
}
