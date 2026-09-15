using Broiler.Dom;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="el"/> is a descendant of
    /// <paramref name="potentialAncestor"/> in the DOM tree.
    /// </summary>
    internal static bool IsDescendantOfElement(DomElement el, DomElement potentialAncestor)
    {
        var current = ParentEl(el);
        while (current != null)
        {
            if (ReferenceEquals(current, potentialAncestor)) return true;
            current = ParentEl(current);
        }
        return false;
    }
}
