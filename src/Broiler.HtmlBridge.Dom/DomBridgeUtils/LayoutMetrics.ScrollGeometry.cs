using Broiler.Dom;
using Broiler.CSS;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    internal static bool IsDocumentElement(DomElement element) =>
        string.Equals(element.TagName, "html", StringComparison.OrdinalIgnoreCase);

    internal static bool IsViewportBodyElement(DomElement element, DomElement documentElement) =>
        string.Equals(element.TagName, "body", StringComparison.OrdinalIgnoreCase) &&
        ReferenceEquals(ParentEl(element), documentElement);

    internal static bool IsDomDescendantOrSelf(DomElement node, DomElement potentialAncestor)
    {
        for (var current = node; current != null; current = ParentEl(current))
        {
            if (ReferenceEquals(current, potentialAncestor))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Picks the side named by <paramref name="longhandName"/> out of the matching
    /// <c>scroll-margin</c>/<c>scroll-padding</c> box shorthand in <paramref name="props"/>,
    /// or null when the shorthand is absent. Logical (<c>-block</c>/<c>-inline</c>) shorthands
    /// are not consulted — only the physical four-side form.
    /// </summary>
    internal static string? ResolveScrollInsetFromShorthand(
        Dictionary<string, string> props, string longhandName)
    {
        var lastDash = longhandName.LastIndexOf('-');
        if (lastDash <= 0)
            return null;

        var shorthandName = longhandName[..lastDash];
        var side = longhandName[(lastDash + 1)..];
        var shorthand = props.GetValueOrDefault(shorthandName);
        if (string.IsNullOrWhiteSpace(shorthand))
            return null;

        var parts = shorthand.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var (top, right, bottom, left) = CssBoxShorthand.SelectTrbl(parts);
        return side switch
        {
            "top" => top,
            "right" => right,
            "bottom" => bottom,
            "left" => left,
            _ => null,
        };
    }
}
