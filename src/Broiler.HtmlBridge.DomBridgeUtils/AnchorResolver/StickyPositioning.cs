namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    internal static bool HasStickyInset(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase);
}
