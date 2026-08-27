using System.Runtime.CompilerServices;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    // -----------------------------------------------------------------
    // position-area resolution for JS offset queries
    // -----------------------------------------------------------------

    // RF-BRIDGE-1b (Milestone 2.5): the resolved position-area rect is memoized here
    // instead of in the retired ElementRuntimeState.Layout (LayoutRuntimeState). It is
    // both a perf cache — avoids rebuilding the anchor registry on every offset query —
    // and a re-entrancy guard: ResolvePositionAreaForElement is reachable from the
    // geometry entry points, so an already-resolved element short-circuits here before
    // re-entering resolution (rebuilding the registry per call recurses / corrupts shared
    // state — the failure mode that reverted the naive cache removal). RF-BRIDGE (Phase 2
    // item 4, 2026-07-17): this memo is now a per-bridge-instance CWT (was process-static),
    // de-globalizing one of the two remaining process-static per-element runtime tables. It
    // is still keyed by element identity, so a detached element's memo is collected with it,
    // and the invalidation (position-area / position-anchor mutation) and clone-copy
    // semantics are unchanged from the old static table.
    private readonly ConditionalWeakTable<DomElement, PositionAreaResolution>
        _positionAreaResolutions = [];

    private sealed class PositionAreaResolution
    {
        public double Left;
        public double Top;
        public double Width;
        public double Height;
    }

    private bool TryGetPositionAreaResolution(
        DomElement element, out (double left, double top, double width, double height) rect)
    {
        if (_positionAreaResolutions.TryGetValue(element, out var r))
        {
            rect = (r.Left, r.Top, r.Width, r.Height);
            return true;
        }

        rect = default;
        return false;
    }

    private void SetPositionAreaResolution(
        DomElement element, double left, double top, double width, double height) =>
        _positionAreaResolutions.AddOrUpdate(element, new PositionAreaResolution
        {
            Left = left,
            Top = top,
            Width = width,
            Height = height,
        });

    internal void ClearPositionAreaResolution(DomElement element) =>
        _positionAreaResolutions.Remove(element);

    private void CopyPositionAreaResolution(DomElement source, DomElement target)
    {
        if (_positionAreaResolutions.TryGetValue(source, out var r))
            _positionAreaResolutions.AddOrUpdate(target, new PositionAreaResolution
            {
                Left = r.Left,
                Top = r.Top,
                Width = r.Width,
                Height = r.Height,
            });
    }

}
