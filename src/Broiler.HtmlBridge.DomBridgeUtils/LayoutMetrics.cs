using Broiler.Dom;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    internal static bool ShouldReportZeroOffsetMetrics(DomElement element) =>
        string.Equals(element.TagName, "map", StringComparison.OrdinalIgnoreCase);
}
