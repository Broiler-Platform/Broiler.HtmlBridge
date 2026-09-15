using Broiler.Dom;

namespace Broiler.HtmlBridge;

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
}
