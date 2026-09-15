namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    /// <summary>
    /// Whether the pseudo rules pin the root's <em>old</em> snapshot over its new one — the
    /// <c>::view-transition-old(root) { opacity: 1 }</c> / <c>-new(root) { opacity: 0 }</c> pairing
    /// the reftests use to freeze a paused transition at its start. Anything else (including the
    /// unpinned default, whose animation would have run to the new state) shows the new state.
    /// </summary>
    internal static bool RootSnapshotShowsOldState(Dictionary<string, Dictionary<string, string>> pseudoRules)
    {
        return IsOpaqueOpacity(OpacityFor("old")) && IsTransparentOpacity(OpacityFor("new"));

        // A name-specific rule wins over the universal one, matching LookupPseudo's cascade order.
        string? OpacityFor(string kind)
        {
            if (pseudoRules.TryGetValue($"{kind}|root", out var byName)
                && byName.TryGetValue("opacity", out var specific))
                return specific;
            return pseudoRules.TryGetValue($"{kind}|*", out var universal)
                && universal.TryGetValue("opacity", out var shared) ? shared : null;
        }
    }

    private static bool IsOpaqueOpacity(string? value) =>
        TryParseOpacity(value, out double opacity) && opacity >= 0.5;

    private static bool IsTransparentOpacity(string? value) =>
        TryParseOpacity(value, out double opacity) && opacity < 0.5;

    private static bool TryParseOpacity(string? value, out double opacity) =>
        double.TryParse(value?.Trim(), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out opacity);
}
