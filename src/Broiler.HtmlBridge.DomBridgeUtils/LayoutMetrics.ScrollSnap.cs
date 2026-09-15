namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    internal static bool IsScrollSnapAlignmentKeyword(string token) =>
        token is "none" or "start" or "end" or "center";
}
