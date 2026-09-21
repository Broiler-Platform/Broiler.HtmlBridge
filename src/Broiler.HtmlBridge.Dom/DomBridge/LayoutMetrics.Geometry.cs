using System;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Broiler.CSS;
using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.HtmlBridge.Logging;
using Broiler.Layout;
using static Broiler.HtmlBridge.DomBridgeHostUtils;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    // The geometry entry points answer *exclusively* from
    // the shared snapshot: an element with a shared box reads its real geometry and any
    // snapshot-missing element (detached, display:none/contents, text/comment, or an
    // unmaterialised/cross-origin frame the provider cannot lay out) reports zero. The coarse
    // LayoutMetrics estimators are deleted, so there is no second source to select between and
    // no flag gating the choice. ElementGeometryBindingModuleTests covers both halves.
    // See docs/architecture/htmlbridge.md#layout-and-geometry.

    private readonly Func<ILayoutView>? _layoutViewFactory;
    private ILayoutView? _layoutView;

    private ILayoutView LayoutView =>
        _layoutView ??= _layoutViewFactory?.Invoke() ?? LayoutViewFactory?.Invoke() ?? NullLayoutView.Instance;

    // Document-scoped teardown: dispose the current view (releasing the renderer's headless
    // container) and drop the per-pass snapshot so a re-attached/re-parsed document lays out
    // fresh. Called from ParseHtml.
    private void DisposeLayoutView()
    {
        _layoutView?.Dispose();
        _layoutView = null;
        _sharedGeometrySnapshot = null;
        DropRetainedGeometrySnapshot();
    }

    // The geometry snapshot for the current WithLayoutGeometryCache read pass. Built
    // lazily on the first shared query and torn down with the pass, so the renderer
    // lays out at most once per pass (one render-projection build) rather than per
    // element — see ClearSharedGeometrySnapshot in WithLayoutGeometryCache.
    private IReadOnlyDictionary<DomElement, BoxGeometry> _sharedGeometrySnapshot;

    /// <summary>
    /// Looks up real-layout box geometry for <paramref name="element"/> via the injected
    /// <see cref="ILayoutView"/>, from the current pass's snapshot (built once
    /// per pass). Returns <c>false</c> when the element produced no box (detached /
    /// <c>display:none</c>); the geometry entry points then report zero, since the coarse
    /// estimators they used to fall back to are gone. Active only when
    /// <see cref="DomBridgeUtils.UseSharedLayoutGeometry"/> is set; the live entry points gate on that.
    /// </summary>
    private bool TryGetSharedLayoutGeometry(DomElement element, out BoxGeometry geometry)
    {
        // The lazy build must run marked as a geometry pass, exactly as WithLayoutGeometryCache
        // marks its own. Building the snapshot creates a render projection, and that projection
        // materialises a running view transition's pseudo tree — which measures the captured
        // elements, i.e. asks for geometry again. ApplyViewTransitionRendering skips itself while a
        // pass is active, so the flag is what terminates the cycle; without it this path recursed
        // BuildSharedGeometrySnapshot → view-transition pseudo tree → getBoundingClientRect →
        // BuildSharedGeometrySnapshot until the stack overflowed.
        //
        // Latent until an await continuation could reach a geometry query mid-transition (the
        // reactions used to race away on the thread pool), and reachable from any non-pass shared
        // query, so it is guarded here rather than at the caller.
        var snapshot = _sharedGeometrySnapshot;
        if (snapshot is null)
        {
            var owner = !_layoutGeometryPassActive;
            if (owner)
                _layoutGeometryPassActive = true;
            try
            {
                snapshot = _sharedGeometrySnapshot ??= AcquireSharedGeometrySnapshot();
            }
            finally
            {
                if (owner)
                    _layoutGeometryPassActive = false;
            }
        }

        return snapshot.TryGetValue(ResolveRenderSource(element), out geometry);
    }

    /// <summary>
    /// Everything a shared geometry snapshot is a function of, so that two queries carrying the same
    /// key are guaranteed to lay out to the same boxes.
    /// </summary>
    /// <remarks>
    /// <see cref="DomDocument.Version"/> covers the DOM the projection is cloned from — every tree
    /// edit, attribute write, text change and adoption bumps it.
    /// <see cref="BridgeRuntimeStateEpoch"/> covers the bridge-side state
    /// <c>CopyBridgeRuntimeStateTo</c> carries into that clone — inline style (which never reaches
    /// the <c>style=</c> attribute at script time), form-control values, CSSOM sheet edits, shadow,
    /// dialog/popover and animation state. The remaining fields are the layout inputs the projection
    /// does not carry: the viewport the renderer is handed, and the two zoom channels
    /// <see cref="BuildSharedGeometrySnapshot"/> sets around it.
    /// </remarks>
    private readonly record struct LayoutSnapshotKey(
        ulong DocumentVersion,
        long BridgeStateEpoch,
        float ViewportWidth,
        float ViewportHeight,
        bool NativeZoom,
        double VisualViewportScale);

    private LayoutSnapshotKey CurrentLayoutSnapshotKey() => new(
        _document.Version,
        BridgeRuntimeStateEpoch.Current,
        _viewportWidth,
        _viewportHeight,
        Broiler.Layout.Engine.NativeZoom.Enabled,
        NativeVisualViewport && HasActiveVisualViewport() ? GetVisualViewportScale() : 0.0);

    // The most recently built snapshot and the key it was built under, held as one immutable pair so
    // that publishing it is a single reference write. Frame actions can run a geometry query on a
    // ThreadPool thread, and a snapshot paired with another build's key is precisely a stale read —
    // the failure this whole cache has to be incapable of. See AcquireSharedGeometrySnapshot.
    private sealed record RetainedGeometry(
        LayoutSnapshotKey Key,
        IReadOnlyDictionary<DomElement, BoxGeometry> Snapshot);

    private RetainedGeometry? _retainedGeometry;

    private void DropRetainedGeometrySnapshot() => _retainedGeometry = null;

    /// <summary>
    /// The snapshot for this read pass: the retained one when nothing it depends on has changed since
    /// it was built, otherwise a fresh layout.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A snapshot costs one whole-document layout, and the per-pass scoping alone bounds that to one
    /// per <em>query</em> — which is one per <c>offsetWidth</c> read and, because clamping a scroll
    /// offset reads four extents, four per <c>scrollTop</c> write. A script that walks a collection
    /// therefore paid a full layout per element per property. WPT's twenty
    /// <c>css/css-overflow/overflow-alignment-*</c> tests do exactly that — 84 scrollers x 2 axes,
    /// against a 1 617-element document that lays out in ~0.9s — and all twenty were reported as
    /// timeouts (issue #1682) rather than as the pixel results they actually are.
    /// </para>
    /// <para>
    /// Retaining the snapshot across passes turns that into one layout for the whole loop. It is sound
    /// exactly while <see cref="LayoutSnapshotKey"/> is unchanged, which is the point of taking the key
    /// from the same inputs the projection is built out of rather than from a "has script run" heuristic.
    /// The key is read <em>after</em> the build, not before: preparing a projection writes bridge runtime
    /// state of its own (<c>CopyBridgeRuntimeStateTo</c> copies every element's state onto its clone, and
    /// the anchor/animation resolvers bake into the overlay), so a key taken before the build would never
    /// match again and the cache would never hit.
    /// </para>
    /// <para>
    /// Conservative in both directions that matter: a read path that has not been routed through
    /// <c>InlineStyleForRead</c> merely bumps the epoch and costs a rebuild, and any state the key does
    /// not model is state the projection does not carry. A stale snapshot needs a layout input to change
    /// with no key field moving — which is why scroll offsets, the one input deliberately excluded, are
    /// argued for at <see cref="RuntimeValue{T}"/> rather than assumed.
    /// </para>
    /// </remarks>
    private IReadOnlyDictionary<DomElement, BoxGeometry> AcquireSharedGeometrySnapshot()
    {
        if (_retainedGeometry is { } retained && retained.Key == CurrentLayoutSnapshotKey())
            return retained.Snapshot;

        var snapshot = BuildSharedGeometrySnapshot();
        _retainedGeometry = new RetainedGeometry(CurrentLayoutSnapshotKey(), snapshot);
        return snapshot;
    }

    private IReadOnlyDictionary<DomElement, BoxGeometry> BuildSharedGeometrySnapshot()
    {
        // Phase-5 LayoutSnapshot endgame — the CSSOM read model uses the engine's used-value `zoom`
        // (increments 1–5), not the serialization bake. Enabling NativeZoom for the snapshot layout makes
        // GetRenderDocument skip the element-`zoom` bake (via ZoomBakeActive) and the engine scale used
        // values instead, so a zoomed element's snapshot geometry — and the unzoomed `offset*`/`client*`
        // the read path divides out of it — is correct for RELATIVE units (`%`, `em`, `rem`, `calc`) too,
        // which the bake mis-scaled (e.g. `width:50%` of a 200px CB under `zoom:2` reported `offsetWidth`
        // 50 instead of 100). Absolute lengths are unchanged (bake and engine already agreed). Thread-static
        // save/restore keeps concurrent layouts unaffected. Because the snapshot never bakes, the live
        // document stays pristine — no revert is needed (the old `_zoomSerializationRevertLog` machinery is
        // retired); the render/serialize path (called directly by capture / WPT, not via this snapshot)
        // still bakes.
        var previousNativeZoom = Broiler.Layout.Engine.NativeZoom.Enabled;
        Broiler.Layout.Engine.NativeZoom.Enabled = true;
        try
        {
            var projection = CreateRenderProjection();
            var viewport = new SizeF(_viewportWidth, _viewportHeight);

            // Native visual-viewport: hand the document-root
            // pinch-zoom scale to the geometry extraction (CollectLayoutGeometry scales the
            // BoxGeometry rects by it — patch 0006) via the thread-static channel, so the snapshot
            // carries the pinch scale natively instead of the DOM `zoom` bake. Thread-static
            // save/restore keeps concurrent layouts unaffected. Off the native path (default) the
            // channel is left at 0 (no scale) and the WPT-runner zoom bake path is untouched.
            var previousVisualViewportScale = Broiler.Layout.Engine.NativeAnchorPlacement.VisualViewportScale;
            Broiler.Layout.Engine.NativeAnchorPlacement.VisualViewportScale =
                NativeVisualViewport && HasActiveVisualViewport() ? GetVisualViewportScale() : 0.0;
            try
            {
                // A materialised iframe/object sub-document is no longer an in-tree
                // #subdoc-root child — hand the layout view the resolver so it projects each
                // referenced content document as a sub-viewport and composes its geometry.
                var projectedGeometry = LayoutView.GetGeometry(
                    projection.Document,
                    viewport,
                    _pageUrl,
                    projectedContainer =>
                    {
                        var source = projection.SourceFor(projectedContainer);
                        return source is null ? null : ResolveContentDocumentForRender(source);
                    });
                var sourceGeometry = new Dictionary<DomElement, BoxGeometry>(ReferenceEqualityComparer.Instance);
                foreach (var (projectedElement, geometry) in projectedGeometry)
                {
                    if (projection.SourceFor(projectedElement) is { } source)
                        sourceGeometry[source] = geometry;
                    else if (!ReferenceEquals(projectedElement.OwnerDocument, projection.Document))
                        // Nested-document geometry is keyed by the resolver's canonical content
                        // document, not by the outer projection. Preserve those live subdocument
                        // identities until nested documents receive their own projection mapping.
                        sourceGeometry[projectedElement] = geometry;
                }
                return sourceGeometry;
            }
            finally
            {
                Broiler.Layout.Engine.NativeAnchorPlacement.VisualViewportScale = previousVisualViewportScale;
            }
        }
        catch
        {
            // Any failure building the shared snapshot (reflection, headless layout,
            // etc.) degrades the whole pass to the estimator — a geometry query must
            // never throw because the renderer choked on the document. This is the
            // bridge's safety net; the injected ILayoutView itself no longer swallows
            // the underlying cause.
            return EmptySharedGeometry;
        }
        finally
        {
            Broiler.Layout.Engine.NativeZoom.Enabled = previousNativeZoom;
            // Projection preparation can populate computed-style caches for detached elements.
            // Drop them after the pass so the projection is not retained by the bridge.
            ClearComputedPropsCache();
        }
    }

    private void ClearSharedGeometrySnapshot() => _sharedGeometrySnapshot = null;

    // Fallback used when no composition root registered a real layout view: geometry queries
    // resolve to an empty map (the same degraded behaviour as a renderer layout failure), so
    // a bridge constructed without the renderer never throws and never references it.
    private sealed class NullLayoutView : ILayoutView
    {
        public static readonly NullLayoutView Instance = new();
        public IReadOnlyDictionary<DomElement, BoxGeometry> GetGeometry(
            DomDocument document, SizeF viewport, string baseUrl,
            Func<DomElement, DomDocument?>? contentDocumentResolver = null) => EmptySharedGeometry;
        public void Dispose() { }
    }
}

/// <summary>
/// Sibling partial peeled out of <c>LayoutMetrics.cs</c> to keep it
/// under the 750-line guideline: CSS <c>&lt;length&gt;</c> / <c>calc()</c>-style math evaluation against a
/// viewport/containing-block basis, and the font-size / line-height reference resolution the length
/// evaluation depends on. Pure partial-class relocation — no signature, accessibility, or logic change.
/// </summary>
public sealed partial class DomBridge
{
    private double ParseCssLengthToPixelsWithViewport(string? value, DomElement? referenceElement = null,
        bool forLineHeight = false, double? percentageBasis = null, bool forFontSize = false)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        return TryEvaluateCssLengthWithViewport(value, referenceElement, forLineHeight, percentageBasis, out var px, forFontSize)
            ? px
            : 0;
    }

    /// <summary>
    /// The one exit every length evaluation passes through, so a non-finite one is refused in a
    /// single place rather than in each unit's branch.
    /// </summary>
    /// <remarks>
    /// Every branch below parses with <see cref="NumberStyles.Float"/>, which accepts .NET's
    /// symbolic forms, and an exponent can overflow a double on its own — so <c>Infinityem</c> and
    /// <c>1e400px</c> both used to arrive here as successful lengths. What is behind this method is
    /// geometry that multiplies and adds what it is given without clamping, so an infinity reaches
    /// script: <c>border-top-width: 1e400px</c> answered <c>element.clientTop === Infinity</c>.
    /// <para>
    /// A guard per branch would have to be repeated for every unit and every future one, and the
    /// recursive <c>calc()</c> paths call back through here, so each part is checked on its own way
    /// out too. An unparseable length and an unrepresentable one are both "not a length".
    /// </para>
    /// </remarks>
    private bool TryEvaluateCssLengthWithViewport(
        string value,
        DomElement? referenceElement,
        bool forLineHeight,
        double? percentageBasis,
        out double result,
        bool forFontSize = false)
    {
        if (!TryEvaluateCssLengthCore(
                value, referenceElement, forLineHeight, percentageBasis, out result, forFontSize) ||
            !double.IsFinite(result))
        {
            result = 0;
            return false;
        }

        return true;
    }

    private bool TryEvaluateCssLengthCore(
        string value,
        DomElement? referenceElement,
        bool forLineHeight,
        double? percentageBasis,
        out double result,
        bool forFontSize = false)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var normalized = value.Trim();
        while (normalized.Length >= 2 &&
               normalized[0] == '(' &&
               normalized[^1] == ')' &&
               CssLengthParser.HasBalancedParens(normalized[1..^1]))
        {
            normalized = normalized[1..^1].Trim();
        }

        if (TryEvaluateMathLengthFunction(normalized, referenceElement, forLineHeight, percentageBasis, out result, forFontSize))
            return true;

        var additiveOperatorIndex = CssLengthParser.FindTopLevelAdditiveOperator(normalized);
        if (additiveOperatorIndex > 0)
        {
            if (!TryEvaluateCssLengthWithViewport(
                    normalized[..additiveOperatorIndex],
                    referenceElement,
                    forLineHeight,
                    percentageBasis,
                    out var left,
                    forFontSize) ||
                !TryEvaluateCssLengthWithViewport(
                    normalized[(additiveOperatorIndex + 1)..],
                    referenceElement,
                    forLineHeight,
                    percentageBasis,
                    out var right,
                    forFontSize))
            {
                return false;
            }

            result = normalized[additiveOperatorIndex] == '+'
                ? left + right
                : left - right;
            return true;
        }

        var lower = normalized.ToLowerInvariant();
        if (percentageBasis.HasValue && lower.EndsWith('%') &&
            double.TryParse(lower[..^1], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var percent))
        {
            result = percentageBasis.Value * (percent / 100.0);
            return true;
        }

        if (referenceElement != null &&
            lower.EndsWith("rem") &&
            double.TryParse(lower[..^3], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var rem))
        {
            result = rem * ResolveFontSizeForLength(referenceElement, rootRelative: true);
            return true;
        }

        if (referenceElement != null &&
            lower.EndsWith("em") &&
            double.TryParse(lower[..^2], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var em))
        {
            // For the font-size property itself, em resolves against the parent's
            // font-size (not the element's own), otherwise resolving the element's
            // font-size would recurse into itself.
            double emBasis;
            if (forFontSize)
            {
                var parent = ParentEl(referenceElement);
                emBasis = parent != null ? ResolveFontSizeForElement(parent) : 16;
            }
            else
            {
                emBasis = ResolveFontSizeForLength(referenceElement, rootRelative: false);
            }

            result = em * emBasis;
            return true;
        }

        if (referenceElement != null &&
            lower.EndsWith("rlh") &&
            double.TryParse(lower[..^3], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var rlh))
        {
            result = rlh * ResolveLineHeightForLength(referenceElement, rootRelative: true);
            return true;
        }

        if (referenceElement != null &&
            lower.EndsWith("lh") &&
            double.TryParse(lower[..^2], NumberStyles.Float,
                CultureInfo.InvariantCulture, out var lh))
        {
            result = lh * ResolveLineHeightForLength(referenceElement, rootRelative: false, forLineHeight);
            return true;
        }

        var px = ParseCssLengthToPixels(normalized, _viewportWidth, _viewportHeight);
        if (double.IsNaN(px))
            return false;

        result = px;
        return true;
    }

    private bool TryEvaluateMathLengthFunction(string value, DomElement? referenceElement,
        bool forLineHeight, double? percentageBasis, out double result,
        bool forFontSize = false)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value) || value[^1] != ')')
            return false;

        static bool StartsWithFunction(string candidate, string functionName)
            => candidate.StartsWith(functionName + "(", StringComparison.OrdinalIgnoreCase);

        if (StartsWithFunction(value, "calc"))
        {
            var content = value[5..^1];
            return CssLengthParser.HasBalancedParens(content) &&
                   TryEvaluateCssLengthWithViewport(content, referenceElement, forLineHeight, percentageBasis, out result, forFontSize);
        }

        if (!StartsWithFunction(value, "min") && !StartsWithFunction(value, "max"))
            return false;

        var isMax = StartsWithFunction(value, "max");
        var contentValue = value[4..^1];
        if (!CssLengthParser.HasBalancedParens(contentValue))
            return false;

        var parts = CssLengthParser.SplitTopLevelArguments(contentValue);
        if (parts.Count == 0)
            return false;

        double? candidate = null;
        foreach (var part in parts)
        {
            if (!TryEvaluateCssLengthWithViewport(part, referenceElement, forLineHeight, percentageBasis, out var parsed, forFontSize))
                return false;

            candidate = candidate.HasValue
                ? (isMax ? Math.Max(candidate.Value, parsed) : Math.Min(candidate.Value, parsed))
                : parsed;
        }

        if (!candidate.HasValue)
            return false;

        result = candidate.Value;
        return true;
    }


    private double ResolveContainingBlockReferenceLength(DomElement element, bool vertical)
    {
        if (ParentEl(element) == null ||
            string.Equals(element.TagName, "html", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(element.TagName, "body", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ParentEl(element).TagName, "html", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ParentEl(element).TagName, "body", StringComparison.OrdinalIgnoreCase))
        {
            return GetViewportReferenceLength(element, vertical);
        }

        var (Left, Top, Width, Height) = ComputeUnzoomedLayoutRect(ParentEl(element));
        var reference = vertical ? Height : Width;
        return reference > 0 ? reference : GetViewportReferenceLength(element, vertical);
    }

    private double GetViewportReferenceLength(DomElement? element, bool vertical)
    {
        if (element != null)
        {
            var documentElement = GetOwningDocumentElement(element);
            var frameElement = GetOuterFrameElement(documentElement);
            if (frameElement != null)
            {
                var frameProps = GetComputedProps(frameElement);
                var frameLength = ParseCssLengthToPixelsWithViewport(
                    frameProps.GetValueOrDefault(vertical ? "height" : "width"),
                    frameElement);
                if (frameLength > 0)
                    return frameLength;

                // TryParseFiniteScalar, not a bare NumberStyles.Float parse: the CSS spelling above
                // reaches the length evaluator's finiteness exit and this one does not, so the
                // attribute was the open half. `> 0` is not that test — it is false for NaN but true
                // for +Infinity, which became the frame's viewport length and answered
                // documentElement.clientWidth === Infinity inside the sub-document. A number this
                // component cannot represent is not a dimension, and a frame with no readable
                // dimension already falls through to the default viewport below.
                if (TryGetAttribute(frameElement, vertical ? "height" : "width", out var frameAttribute) &&
                    TryParseFiniteScalar(frameAttribute, out frameLength) &&
                    frameLength > 0)
                {
                    return frameLength;
                }
            }
        }

        return vertical ? _viewportHeight : _viewportWidth;
    }

    private double ResolveLineHeightForLength(DomElement element, bool rootRelative, bool forLineHeight = false)
    {
        var target = rootRelative ? GetRootElement(element) : (forLineHeight ? ParentEl(element) ?? element : element);
        return ResolveLineHeightForElement(target);
    }

    private double ResolveFontSizeForLength(DomElement element, bool rootRelative)
    {
        var target = rootRelative ? GetRootElement(element) : element;
        return ResolveFontSizeForElement(target);
    }

    private DomElement GetRootElement(DomElement element)
    {
        DomElement? htmlElement = null;
        var current = element;
        while (ParentEl(current) != null)
        {
            current = ParentEl(current);
            if (string.Equals(current.TagName, "html", StringComparison.OrdinalIgnoreCase))
                htmlElement = current;
        }

        return htmlElement ?? current;
    }

    /// <summary>
    /// The one exit every line-height resolution passes through, so a line height that cannot be
    /// represented is refused once rather than in each of the three spellings below.
    /// </summary>
    /// <remarks>
    /// The unitless multiplier is the spelling that needs it. It is the only one that does not
    /// reach <see cref="TryEvaluateCssLengthWithViewport"/>, so the finiteness exit that method
    /// gained never covered it, and it is parsed with <see cref="NumberStyles.Float"/> — which
    /// admits <c>Infinity</c> and <c>NaN</c> by name and overflows a double on an exponent, or on
    /// a long enough run of digits, with no symbol in the value at all.
    /// <para>
    /// Testing the resolved height rather than the token also covers the multiplication, which is
    /// the half a guard at the parse would have missed: <c>line-height: 1e307</c> is a multiplier
    /// a double holds perfectly well, and <c>16px</c> times it is not.
    /// </para>
    /// <para>
    /// A refused line height is <c>normal</c> — the same <c>1.2</c> factor the unset and
    /// <c>normal</c> cases take — and deliberately not zero. Zero is a line height a page can
    /// write, and substituting it would make a value this component refused indistinguishable
    /// from one the page chose. The font size is finite by construction (
    /// <see cref="ResolveFontSizeForElement"/> resolves through the guarded evaluator and falls
    /// back to <c>16</c>), so the refusal cannot itself answer with a non-finite number.
    /// </para>
    /// </remarks>
    private double ResolveLineHeightForElement(DomElement element)
    {
        var resolved = ResolveLineHeightForElementCore(element, out var fontSize);
        return double.IsFinite(resolved) ? resolved : fontSize * 1.2;
    }

    private double ResolveLineHeightForElementCore(DomElement element, out double fontSize)
    {
        var props = GetComputedProps(element);
        fontSize = ResolveFontSizeForElement(element);
        var lineHeight = props.GetValueOrDefault("line-height");
        if (string.IsNullOrWhiteSpace(lineHeight) ||
            string.Equals(lineHeight, "normal", StringComparison.OrdinalIgnoreCase))
        {
            return fontSize * 1.2;
        }

        var normalized = lineHeight.Trim().ToLowerInvariant();
        if (double.TryParse(normalized, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var multiplier))
        {
            return fontSize * multiplier;
        }

        return ParseCssLengthToPixelsWithViewport(lineHeight, element, forLineHeight: true);
    }

    private double ResolveFontSizeForElement(DomElement element)
    {
        var props = GetComputedProps(element);
        var fontSize = ParseCssLengthToPixelsWithViewport(props.GetValueOrDefault("font-size"), element, forFontSize: true);
        if (fontSize > 0)
            return fontSize;

        for (var current = element; current != null; current = ParentEl(current))
        {
            if (!TryGetAttribute(current, "font-size", out var attributeValue) ||
                string.IsNullOrWhiteSpace(attributeValue))
            {
                continue;
            }

            var attributeFontSize = ParseCssLengthToPixelsWithViewport(attributeValue, current, forFontSize: true);
            if (attributeFontSize > 0)
                return attributeFontSize;
        }

        return 16;
    }

}

/// <summary>
/// CSS 2D transform geometry for <c>getBoundingClientRect</c>: applies an element's own
/// <c>transform</c> and every transformed ancestor's to the element's border box, returning the
/// axis-aligned visual rect. Layout is computed without transforms (they are a paint-time visual
/// effect), so the snapshot border boxes — and therefore <c>offsetWidth</c>/<c>offset*</c> — stay
/// untransformed; only the bounding-client-rect path composes this chain on top.
/// </summary>
public sealed partial class DomBridge
{
    /// <summary>
    /// Transforms the border box's four corners by the element's own transform and each transformed
    /// ancestor's — innermost first, each about its own transform-origin in document space — and
    /// returns the enclosing axis-aligned rect. A chain with no transforms returns the box unchanged.
    /// </summary>
    private (double Left, double Top, double Width, double Height) ApplyTransformChain(
        DomElement element, (double Left, double Top, double Width, double Height) box)
    {
        Span<double> xs = [box.Left, box.Left + box.Width, box.Left + box.Width, box.Left];
        Span<double> ys = [box.Top, box.Top, box.Top + box.Height, box.Top + box.Height];

        var transformed = false;
        for (DomElement? current = element; current is not null; current = ParentEl(current))
        {
            // The chain stops at a <foreignObject>. Above it the ancestors are SVG elements whose
            // `transform` is a user-space mapping, not a CSS transform on a box, and the element's
            // box has already been placed at the position that mapping puts it (see
            // SvgForeignObjectBoxes) — walking on would apply the same translate a second time, so
            // a <div> inside a <g transform="translate(100,50)"> reported itself 100,50 further on
            // than the <foreignObject> that contains it.
            if (SvgLocalName(current) == "foreignobject")
                break;

            var transformValue = GetElementTransformValue(current);
            if (string.IsNullOrWhiteSpace(transformValue))
                continue;

            var (originX, originY) = GetTransformOriginDocumentSpace(current, out var boxWidth, out var boxHeight);
            var matrix = ParseTransformFunctions(transformValue, boxWidth, boxHeight);
            if (matrix.IsIdentity)
                continue;

            for (var i = 0; i < 4; i++)
            {
                var dx = xs[i] - originX;
                var dy = ys[i] - originY;
                xs[i] = matrix.A * dx + matrix.C * dy + originX + matrix.E;
                ys[i] = matrix.B * dx + matrix.D * dy + originY + matrix.F;
            }
            transformed = true;
        }

        if (!transformed)
            return box;

        // The chain's own exit. Every matrix folded above is finite — ParseTransformFunctions
        // refuses one that is not — but the point the chain rotates each box about is not part of
        // the matrix: `transform-origin` is resolved by the shared grammar
        // (Layout.IR.CssTransformOrigin), so `transform-origin: 1e400px` arrives here past every
        // guard on the transform value and sends the corners to infinity by itself. Rather than
        // testing that one input, the corners are tested, which covers the origin, the box the
        // caller handed in, and whatever a later ancestor kind contributes.
        //
        // A chain that cannot place the box contributes nothing and the element reports its
        // untransformed border box — the same rect `transform: none` answers, and the same answer
        // a refused function gives one level down.
        for (var i = 0; i < 4; i++)
        {
            if (!double.IsFinite(xs[i]) || !double.IsFinite(ys[i]))
                return box;
        }

        double minX = xs[0], maxX = xs[0], minY = ys[0], maxY = ys[0];
        for (var i = 1; i < 4; i++)
        {
            minX = Math.Min(minX, xs[i]);
            maxX = Math.Max(maxX, xs[i]);
            minY = Math.Min(minY, ys[i]);
            maxY = Math.Max(maxY, ys[i]);
        }
        return (minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>The element's transform-origin in document space — its untransformed border-box
    /// position plus the resolved origin offset (default <c>50% 50%</c>). The box's zoomed size is
    /// also returned for resolving percentage translations against the same box.</summary>
    private (double X, double Y) GetTransformOriginDocumentSpace(DomElement element, out double boxWidth, out double boxHeight)
    {
        var (left, top, width, height) = ComputeUnzoomedLayoutRect(element);
        var zoom = GetUsedZoomForElement(element);
        boxWidth = width * zoom;
        boxHeight = height * zoom;

        var origin = GetComputedProps(element).GetValueOrDefault("transform-origin");
        var (offsetX, offsetY) = ParseTransformOrigin(origin, boxWidth, boxHeight);
        return (left + offsetX, top + offsetY);
    }
}
