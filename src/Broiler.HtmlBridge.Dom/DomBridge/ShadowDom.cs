using System.Runtime.CompilerServices;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.Runtime;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    // Phase 2 item 4 (de-globalization, 2026-07-17): the per-element shadow-DOM linkage (a host's
    // shadow root, a root's host, and the root's mode) was the Shadow slot of the process-static
    // ElementRuntimeState table; it is now a per-bridge instance table, owned by the session's bridge.
    // Still element-keyed, so it GCs with the element and the cloneNode copy (see CloneDomElement) is
    // preserved. The former static GetShadowRoot / GetShadowHost helpers became instance methods (all
    // their callers were already on the bridge instance), so no cross-class host threading was needed.
    private readonly ConditionalWeakTable<DomElement, ShadowRuntimeState> _shadowRuntimeStates = [];

    private ShadowRuntimeState ShadowStateFor(DomElement element) =>
        _shadowRuntimeStates.GetValue(element, static _ => new ShadowRuntimeState());

    private DomElement? GetShadowRoot(DomElement element)
    {
        if (ShadowStateFor(element).Root.TryGet(out var rawShadowRoot) &&
            rawShadowRoot is DomElement root)
        {
            return root;
        }

        return null;
    }

    private DomElement? GetShadowHost(DomElement? shadowRoot)
    {
        if (shadowRoot != null &&
            string.Equals(shadowRoot.TagName, "#shadow-root", StringComparison.Ordinal) &&
            ShadowStateFor(shadowRoot).Host.TryGet(out var rawHost) &&
            rawHost is DomElement host)
        {
            return host;
        }

        return null;
    }

    internal static DomElement? FindContainingShadowRoot(DomNode? node)
    {
        for (var current = node; current != null; current = current.ParentNode)
        {
            if (current is DomElement element && string.Equals(element.TagName, "#shadow-root", StringComparison.Ordinal))
                return element;
        }

        return null;
    }

    // Walk to the absolute root. For a connected node this is the canonical DomDocument (Phase 4: the
    // document root is the DomDocument, not a #document wrapper element); a detached subtree roots to its
    // topmost node. Phase 4 item 4/5: this is exactly canonical DomNode.GetRootNode(), so delegate to it
    // rather than re-implement the `while ParentNode` climb.
    private DomNode GetTreeRoot(DomNode node) => node.GetRootNode();

    private JSValue ToJSRootNode(DomNode root)
    {
        if (ReferenceEquals(root, _document))
            return _documentJSObject ?? JSNull.Value;

        // A severed sub-document root is a canonical DomDocument (P4.4b); ToJSObject resolves it to
        // its document wrapper via the document-wrapper map, so no #subdoc-root special case remains.
        return ToJSObject(root);
    }

    private DomElement? GetSlotHost(DomElement slot) => GetShadowHost(FindContainingShadowRoot(slot));

    private static bool SlotAcceptsNode(DomElement slot, DomElement node)
    {
        var slotName = GetAttr(slot, "name");
        var nodeSlot = GetAttr(node, "slot");
        return string.IsNullOrEmpty(slotName)
            ? string.IsNullOrEmpty(nodeSlot)
            : string.Equals(slotName, nodeSlot, StringComparison.OrdinalIgnoreCase);
    }

    private DomElement? FindAssignedSlot(DomElement root, DomElement node)
    {
        foreach (var child in ChildElements(root))
        {
            if (IsText(child))
                continue;

            if (string.Equals(child.TagName, "slot", StringComparison.OrdinalIgnoreCase) &&
                SlotAcceptsNode(child, node))
            {
                return child;
            }

            var nested = FindAssignedSlot(child, node);
            if (nested != null)
                return nested;
        }

        return null;
    }

    private DomElement? GetAssignedSlot(DomElement element)
    {
        if (IsText(element) || ParentEl(element) == null)
            return null;

        var shadowRoot = GetShadowRoot(ParentEl(element));
        return shadowRoot != null ? FindAssignedSlot(shadowRoot, element) : null;
    }

    private DomElement? GetScrollTraversalParent(DomElement element)
    {
        var assignedSlot = GetAssignedSlot(element);
        if (assignedSlot != null)
            return assignedSlot;

        var parent = ParentEl(element);
        return GetShadowHost(parent) ?? parent;
    }
}
