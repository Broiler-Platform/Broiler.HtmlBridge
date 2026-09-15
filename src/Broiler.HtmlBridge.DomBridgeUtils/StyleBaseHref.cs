using Broiler.Dom;

namespace Broiler.HtmlBridge;

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
