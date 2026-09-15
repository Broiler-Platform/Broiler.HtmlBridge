using Broiler.Dom;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    /// <summary>Whether <paramref name="element"/> is an HTML <c>&lt;template&gt;</c>.</summary>
    internal static bool IsTemplateElement(DomElement element) =>
        string.Equals(element.TagName, "template", StringComparison.OrdinalIgnoreCase);
}
