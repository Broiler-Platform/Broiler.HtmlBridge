using Broiler.Dom;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    internal static string NormalizeScrollIntoViewAlignment(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
            return fallback;

        var normalized = value.Trim().ToLowerInvariant();
        return normalized is "start" or "center" or "end" or "nearest" or "start-if-needed" or "end-if-needed"
            ? normalized
            : fallback;
    }

    internal static string ResolvePhysicalAxisAlignment(string? alignment, bool startMapsToPhysicalStart)
    {
        var normalized = NormalizeScrollIntoViewAlignment(alignment, "start");
        if (normalized is "center" or "nearest" || startMapsToPhysicalStart)
            return normalized;

        return normalized switch
        {
            "start" => "end",
            "end" => "start",
            "start-if-needed" => "end-if-needed",
            "end-if-needed" => "start-if-needed",
            _ => normalized
        };
    }

    internal static string NormalizeScrollBehavior(string? behavior)
    {
        if (string.IsNullOrWhiteSpace(behavior))
            return "auto";

        var normalized = behavior.Trim().ToLowerInvariant();
        return normalized is "instant" or "smooth" ? normalized : "auto";
    }

    internal static bool AreClose(double left, double right) => Math.Abs(left - right) < 0.0001;

    internal static string? GetOverflowAxisValue(Dictionary<string, string> props, bool vertical)
    {
        var axisValue = props.GetValueOrDefault(vertical ? "overflow-y" : "overflow-x");
        if (string.IsNullOrWhiteSpace(axisValue))
            axisValue = props.GetValueOrDefault("overflow");
        return axisValue;
    }

    /// <summary>
    /// Whether a root-propagated <c>overflow</c> value makes the viewport non-scrollable.
    /// Only <c>clip</c> does: it suppresses the scroll container entirely (CSS Overflow 3
    /// §3.3), so the scrollport has no scroll offset to set.
    /// <para><c>overflow: hidden</c> does <em>not</em> belong here. It still establishes a
    /// scroll container — it only removes the <em>user-interaction</em> affordance, while
    /// programmatic scrolling (<c>scrollTop</c>/<c>scrollLeft</c>, <c>scrollTo</c>,
    /// <c>scrollIntoView</c>) keeps working. Treating it as non-scrollable pinned every such
    /// document at offset 0, which silently broke the very common WPT reftest idiom
    /// <c>:root { overflow: hidden; /* hide scrollbars for reftest analysis */ }</c> on any
    /// test that also scrolls (issue #1439: css-scroll-snap/scroll-snap-root-001 and -002
    /// both rendered their unscrolled red FAIL block).</para>
    /// </summary>
    internal static bool DisablesRootScrolling(string? overflowValue)
    {
        if (string.IsNullOrWhiteSpace(overflowValue))
            return false;

        return overflowValue.Trim().ToLowerInvariant().Contains("clip");
    }

    internal static bool EnablesScrollingBox(string? overflowValue)
    {
        if (string.IsNullOrWhiteSpace(overflowValue))
            return false;

        var value = overflowValue.Trim().ToLowerInvariant();
        return value.Contains("hidden") || value.Contains("scroll") || value.Contains("auto") || value.Contains("clip");
    }

    internal static int CountSelectOptions(DomElement element)
    {
        int count = 0;
        foreach (var child in ChildElements(element).Where(c => !IsText(c)))
        {
            if (string.Equals(child.TagName, "option", StringComparison.OrdinalIgnoreCase))
            {
                count++;
                continue;
            }

            count += CountSelectOptions(child);
        }

        return count;
    }
}
