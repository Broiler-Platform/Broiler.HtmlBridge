using Broiler.Dom;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    // -----------------------------------------------------------------
    // Shared anchor-lookup helpers
    // -----------------------------------------------------------------
    //
    // The bridge's ResolvePositionVisibility pass — which baked display:none onto
    // anchor-positioned elements whose anchor was scrolled out / invalid — was deleted in
    // Phase 4 item-2 step 3. The Broiler.Layout engine resolves position-visibility natively
    // (CssBox.ResolvePositionVisibility), so no bridge pre-bake is needed on the native
    // (now-default) path. The anchor-lookup helpers below stay: PositionArea / AnchorRegistry
    // still use them to bind targets to their anchor and containing block.

    /// <summary>
    /// Finds the <see cref="Broiler.Dom.DomElement"/> that has the given
    /// <c>anchor-name</c> (from CSS rules or inline styles).
    /// </summary>
    private DomElement? FindElementByAnchorName(string anchorName)
    {
        foreach (var el in Elements)
        {
            if (IsText(el)) continue;
            // Check inline styles first.
            if (BakedInlineStyle(el).TryGetValue("anchor-name", out var n) &&
                string.Equals(n.Trim(), anchorName, StringComparison.Ordinal))
                return el;
        }

        // Fall back to the shared cascade.
        foreach (var el in Elements)
        {
            if (IsText(el)) continue;
            var declarations = CollectMatchedRuleProperties(el);
            if (declarations.TryGetValue("anchor-name", out var name) &&
                string.Equals(name.Trim(), anchorName, StringComparison.Ordinal))
                return el;
        }
        return null;
    }

    /// <summary>
    /// Finds the nearest positioned ancestor that serves as the containing
    /// block for an absolutely positioned element.
    /// </summary>
    private DomElement? FindContainingBlockElement(DomElement el)
    {
        var parent = ParentEl(el);
        while (parent != null)
        {
            var pProps = GetComputedProps(parent);
            if (EstablishesContainingBlock(pProps))
                return parent;
            parent = ParentEl(parent);
        }
        return null;
    }

    /// <summary>
    /// Finds the containing block for the anchor referenced by the target element.
    /// The anchor's CB is typically the same as the target's CB when both are
    /// inside the same positioned ancestor.
    /// </summary>
    private DomElement? FindAnchorContainingBlock(DomElement target, DomElement targetCB)
    {
        // Find the anchor element by looking at the target's position-anchor.
        var cssProps = GetComputedProps(target);
        string? posAnchor = cssProps.GetValueOrDefault("position-anchor");
        if (string.IsNullOrWhiteSpace(posAnchor)) return null;

        var anchorEl = FindElementByAnchorName(posAnchor);
        if (anchorEl == null) return null;

        return FindContainingBlockElement(anchorEl);
    }
}
