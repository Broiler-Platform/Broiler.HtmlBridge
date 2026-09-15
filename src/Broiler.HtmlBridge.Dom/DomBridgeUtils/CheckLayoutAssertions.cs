using System.Text;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    // Maps the WPT check-layout-th.js data-* attributes this evaluator understands
    // to a short property label. Restricted to the box metrics the bridge computes
    // directly (offset / client box); scroll and bounding-client-rect checks are not
    // yet covered.
    internal static readonly (string Attribute, string Property)[] CheckLayoutAttributeMap =
    [
        ("data-offset-x", "offset-x"),
        ("data-offset-y", "offset-y"),
        ("data-expected-width", "width"),
        ("data-expected-height", "height"),
        ("data-expected-client-width", "client-width"),
        ("data-expected-client-height", "client-height"),
        ("data-total-x", "total-x"),
        ("data-total-y", "total-y"),
    ];

    /// <summary>Concise CSS-ish descriptor for reporting (tag + id/first-class + title).</summary>
    internal static string DescribeElement(Broiler.Dom.DomElement element)
    {
        var builder = new StringBuilder(element.TagName.ToLowerInvariant());
        if (!string.IsNullOrEmpty(element.Id))
        {
            builder.Append('#').Append(element.Id);
        }
        else if (!string.IsNullOrEmpty(element.ClassName))
        {
            var firstClass = element.ClassName
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (firstClass is not null)
                builder.Append('.').Append(firstClass);
        }

        if (TryGetAttribute(element, "title", out var title) && !string.IsNullOrEmpty(title))
            builder.Append("[title=").Append(title).Append(']');

        return builder.ToString();
    }
}
