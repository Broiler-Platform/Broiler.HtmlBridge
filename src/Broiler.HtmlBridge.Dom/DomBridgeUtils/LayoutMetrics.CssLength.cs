namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    private static bool HasExplicitBodyMargin(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               !string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase);
    }
}
