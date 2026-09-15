using System.Text;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    /// <summary>
    /// Recursively collects text content from script elements.
    /// </summary>
    internal static void CollectScriptContent(DomElement element, List<string> scripts)
    {
        if (string.Equals(element.TagName, "script", StringComparison.OrdinalIgnoreCase))
        {
            var text = GetTextContentRecursive(element);
            if (!string.IsNullOrWhiteSpace(text))
                scripts.Add(text);
            return;
        }

        foreach (var child in ChildElements(element))
            CollectScriptContent(child, scripts);
    }

    /// <summary>
    /// Gets the concatenated text content of an element and all its descendants.
    /// </summary>
    internal static string GetTextContentRecursive(DomElement element)
    {
        // RF-BRIDGE-1c Phase F (F3c part 2d): aggregate descendant text over raw ChildNodes.
        var sb = new StringBuilder();
        CollectTextContent(element, sb);
        return sb.ToString();
    }
}
