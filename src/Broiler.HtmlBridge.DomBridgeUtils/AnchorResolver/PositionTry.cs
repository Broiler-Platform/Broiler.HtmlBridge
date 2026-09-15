using System.Globalization;
using Broiler.CSS;
using Broiler.Layout;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    internal static string ResolveAnchorEdge(AnchorFunctionRef reference, Dictionary<string, AnchorInfo> registry,
        string contextProp, double cbWidth, double cbHeight, string? implicitAnchor = null)
    {
        var anchorName = string.IsNullOrEmpty(reference.Name)
            ? (implicitAnchor ?? string.Empty)
            : reference.Name!;

        if (!registry.TryGetValue(anchorName, out var anchor))
            return "0px";

        // Edge coordinate math (no scroll adjustment on the fallback path) is the
        // canonical Broiler.Layout.AnchorGeometry model (Phase 5 item 3).
        double value = AnchorGeometry.ResolveEdge(
            anchor.Left, anchor.Top, anchor.Right, anchor.Bottom,
            reference.Side, 0, 0, MapAnchorInsetProperty(contextProp), cbWidth, cbHeight);

        return $"{value.ToString(CultureInfo.InvariantCulture)}px";
    }
}
