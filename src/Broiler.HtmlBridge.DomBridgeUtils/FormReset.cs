using Broiler.Dom;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    internal static bool IsRadioInput(DomElement element) =>
        string.Equals(element.TagName, "input", StringComparison.OrdinalIgnoreCase) &&
        TryGetAttribute(element, "type", out var type) &&
        string.Equals(type, "radio", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The radio-group scope for <paramref name="element"/>: its form owner, or the root of its tree
    /// when it has none (HTML defines the group over the form owner, falling back to the tree).
    /// </summary>
    internal static DomElement RadioGroupScope(DomElement element)
    {
        var scope = ParentEl(element);
        while (scope != null && !string.Equals(scope.TagName, "form", StringComparison.OrdinalIgnoreCase))
            scope = ParentEl(scope);

        if (scope != null)
            return scope;

        scope = element;
        while (ParentEl(scope) != null)
            scope = ParentEl(scope);
        return scope;
    }
}
