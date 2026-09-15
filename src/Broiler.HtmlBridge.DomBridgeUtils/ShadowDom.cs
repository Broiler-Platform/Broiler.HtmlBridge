using Broiler.Dom;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    internal static DomElement? FindContainingShadowRoot(DomNode? node)
    {
        for (var current = node; current != null; current = current.ParentNode)
        {
            if (current is DomElement element && string.Equals(element.TagName, "#shadow-root", StringComparison.Ordinal))
                return element;
        }

        return null;
    }

    internal static bool SlotAcceptsNode(DomElement slot, DomElement node)
    {
        var slotName = GetAttr(slot, "name");
        var nodeSlot = GetAttr(node, "slot");
        return string.IsNullOrEmpty(slotName)
            ? string.IsNullOrEmpty(nodeSlot)
            : string.Equals(slotName, nodeSlot, StringComparison.OrdinalIgnoreCase);
    }
}
