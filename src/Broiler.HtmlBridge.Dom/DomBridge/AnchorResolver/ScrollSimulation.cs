using System.Globalization;
using Broiler.Dom;
using Broiler.CSS;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    // -----------------------------------------------------------------
    // Scroll simulation
    // -----------------------------------------------------------------

    /// <summary>
    /// Simulates scroll positions set via JavaScript (<c>element.scrollTop</c>,
    /// <c>element.scrollLeft</c>) by shifting children of scroll containers
    /// with negative margins.  Combined with <c>overflow: hidden</c>, this
    /// produces the same visual output as a real browser scroll.
    /// </summary>
    private void ApplyScrollSimulation(DomElement root) =>
        ApplyScrollSimulationTree(root, GetScrollSimulationScaleFactor());

    /// <summary>
    /// The same pass for every nested browsing context this session has materialised.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A frame's document is severed from the main tree (P4.4b), so walking
    /// <see cref="DocumentElement"/> never reaches it: a frame whose script had scrolled something
    /// serialized with none of that state, and the capture showed the frame at its initial scroll
    /// position. The scroll offset was recorded correctly — <c>scrollTop</c> read back the value
    /// <c>scrollIntoView</c> had set — it simply never reached the markup.
    /// </para>
    /// <para>
    /// Sound to re-run per frame for the reason the top-layer passes are
    /// (<see cref="ApplySubDocumentTopLayer"/>): everything this reads is already per-element or
    /// per-document — the recorded scroll state, and a computed <c>overflow</c> resolved by that
    /// document's own style scope — and it needs no geometry, which is the thing this bridge only
    /// measures for the main frame.
    /// </para>
    /// <para>
    /// The visual-viewport scale is deliberately not applied inside a frame. Pinch zoom scales the
    /// frame's box as a whole, so scaling the offset the frame scrolled *within* itself would count
    /// the same zoom twice.
    /// </para>
    /// </remarks>
    private void ApplySubDocumentScrollSimulation()
    {
        // Snapshot: reading a computed style can materialise a further frame, which would otherwise
        // mutate the map mid-iteration.
        foreach (var contentDocument in _browsingContexts.ContentDocuments.ToList())
        {
            if (GetDocumentElement(contentDocument) is { } subRoot)
                ApplyScrollSimulationTree(subRoot, scrollScale: 1);
        }
    }

    private void ApplyScrollSimulationTree(DomElement el, double scrollScale)
    {
        if (!IsText(el))
        {
            double scrollTop = 0;
            double scrollLeft = 0;
            if (ScrollStateFor(el).Top.TryGet(out var st) && st is double stv)
                scrollTop = stv;
            if (ScrollStateFor(el).Left.TryGet(out var sl) && sl is double slv)
                scrollLeft = slv;

            if (!AreClose(scrollScale, 1))
            {
                scrollTop *= scrollScale;
                scrollLeft *= scrollScale;
            }

            if (scrollTop != 0 || scrollLeft != 0)
            {
                // Only apply to elements that clip overflow, or to the
                // document scrolling element (<html>) which is implicitly
                // clipped by the viewport.
                var props = GetComputedProps(el);
                bool clips = CssOverflow.ClipsOverflow(props);
                bool isDocScrollingElement =
                    string.Equals(el.TagName, "html", StringComparison.OrdinalIgnoreCase);

                if ((clips || isDocScrollingElement) && el.ChildNodes.Count > 0)
                {
                    // Hand the scroll offset to the Broiler.Layout engine via data attributes
                    // instead of DOM-shifting the content. The engine's scroll post-pass
                    // (CssBox.RunScrollSimulation) translates the container's content and its
                    // overflow box (or the viewport, for the document scrolling element) clips it —
                    // no wrapper div, no inline position/top/left/visibility writes, and no
                    // fixed-descendant reparenting (OffsetTop/OffsetLeft skip position:fixed at every
                    // depth, CSS2.1 §9.6.1). The document scrolling element (<html>) is included:
                    // with a scrollable root (tall content) documentElement.scrollTop resolves
                    // normally and the engine translation matches. The flag check is dropped in
                    // Phase 4 item-2 step 5 — the handoff is unconditional (a provable no-op on the
                    // native default path, where the flag was already true); the retired baked
                    // DOM-shift wrapper (and its scroll-hidden / anchor-cb markers) is deleted.
                    if (scrollTop != 0)
                        SetAttr(el, "data-broiler-scroll-top",
                            scrollTop.ToString(CultureInfo.InvariantCulture));
                    if (scrollLeft != 0)
                        SetAttr(el, "data-broiler-scroll-left",
                            scrollLeft.ToString(CultureInfo.InvariantCulture));

                    // Recurse into children (nested scroll containers) and skip the DOM-shift.
                    for (int i = 0; i < el.ChildNodes.Count; i++)
                        if (ChildAt(el, i) is DomElement scrolledChild)
                            ApplyScrollSimulationTree(scrolledChild, scrollScale);
                    return;
                }
            }
        }

        // Use index-based loop because the list may grow during iteration
        // (wrapper insertion above).
        for (int i = 0; i < el.ChildNodes.Count; i++)
            if (ChildAt(el, i) is DomElement child)
                ApplyScrollSimulationTree(child, scrollScale);
    }

    private double GetScrollSimulationScaleFactor() => HasActiveVisualViewport() ? GetVisualViewportScale() : 1;

}
