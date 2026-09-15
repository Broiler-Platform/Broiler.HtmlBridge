using Broiler.Layout;
using Broiler.Dom;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    // RF-BRIDGE-1b: when true, element-geometry queries (offset*/client*/
    // getBoundingClientRect/check-layout) resolve through the renderer's real layout
    // engine via the injected ILayoutView instead of the coarse LayoutMetrics
    // estimators. Enabled once increments 1-3 landed (the LayoutMetrics entry points and
    // the anchor resolver route through the provider, and the Broiler.HTML inline-box
    // geometry fix is live on CI) and the increment-4 parity gate confirmed the shared
    // path matches or improves on the estimators — see
    // SharedLayoutGeometryParityTests.Shared_Geometry_Matches_Or_Beats_Estimator_On_CheckLayout_Corpus.
    internal static bool UseSharedLayoutGeometry = true;

    // The preferred binding is the per-session factory supplied through DomBridgeSessionOptions,
    // which keeps simultaneous documents independent. This process-static factory remains only
    // as a source-compatibility fallback for composition roots that have not migrated yet. A bare
    // `new DomBridge()` with neither factory falls back to an empty view and does not pull in the
    // concrete renderer stack.
    internal static Func<ILayoutView>? LayoutViewFactory;

    internal static readonly IReadOnlyDictionary<DomElement, BoxGeometry> EmptySharedGeometry =
        new Dictionary<DomElement, BoxGeometry>();
}
