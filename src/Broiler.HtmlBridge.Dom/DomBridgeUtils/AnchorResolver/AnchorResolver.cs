using System.Text.RegularExpressions;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    /// <summary>
    /// Tags whose rendering is already replaced (or which generate no box at all), so CSS
    /// Content 3 element replacement does not apply to them here.
    /// </summary>
    internal static readonly HashSet<string> ContentReplacementSkipTags =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "area", "audio", "base", "br", "canvas", "col", "embed", "head", "hr", "iframe",
            "img", "input", "link", "meta", "object", "param", "script", "select", "source",
            "style", "textarea", "title", "track", "video",
        };

    /// <summary>
    /// Extracts the target of a CSS <c>url(...)</c> value (used by the root
    /// element-replacement path).  Returns <c>null</c> for non-<c>url()</c>
    /// content (strings, counters, <c>normal</c>/<c>none</c>).
    /// </summary>
    internal static string? ExtractContentImageUrl(string content)
    {
        var match = ExtractContentImageUrlRegex().Match(content);
        return match.Success ? match.Groups["u"].Value.Trim() : null;
    }

    [GeneratedRegex(@"url\(\s*(['""]?)(?<u>[^'""\)]+)\1\s*\)", RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ExtractContentImageUrlRegex();
}
