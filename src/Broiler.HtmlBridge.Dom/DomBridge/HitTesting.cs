using System.Globalization;
using System.Runtime.CompilerServices;
using Broiler.CSS;
using Broiler.Dom;
using Broiler.HtmlBridge.Dom.Runtime;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge
{
    // The per-document-root "has an explicit viewport
    // meta" flag was the Document slot of the process-static ElementRuntimeState table; it is now a
    // per-bridge instance table, owned by the session's bridge. Still an element-keyed
    // ConditionalWeakTable, so it GCs with the document root and the cloneNode copy (see
    // CloneDomElement) is preserved. Both access sites are on the bridge instance — no cascade.
    // Keyed by DomNode (not DomElement): the "has viewport" flag is recorded against the document
    // node (a DomDocument) as well as document-root elements, matching the former DomNode-keyed table.
    private readonly ConditionalWeakTable<DomNode, DocumentRuntimeState> _documentRuntimeStates = [];

    private DocumentRuntimeState DocumentStateFor(DomNode node) =>
        _documentRuntimeStates.GetValue(node, static _ => new DocumentRuntimeState());

    private IReadOnlyList<DomElement> HitTestDocumentPoint(DomNode docRoot, double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y))
            return [];

        // docRoot may be a canonical DomDocument (regime-B) — resolve the
        // documentElement from its element children; only an element docRoot can itself be one.
        var documentElement = docRoot is DomElement docRootElement && IsDocumentElement(docRootElement)
            ? docRootElement
            : ChildElements(docRoot).FirstOrDefault(c => !c.TagName.StartsWith('#'));

        if (documentElement == null)
            return [];

        if (!DocumentHasViewport(documentElement))
            return [];

        var viewportWidth = GetViewportReferenceLength(documentElement, vertical: false);
        var viewportHeight = GetViewportReferenceLength(documentElement, vertical: true);
        if (viewportWidth <= 0 || viewportHeight <= 0 || x < 0 || y < 0 || x >= viewportWidth || y >= viewportHeight)
            return [];

        // One geometry pass for the whole walk, not one per element. CollectHitTestMatches asks
        // every element in the tree for its border box, and each of those reads went through
        // WithLayoutGeometryCache with no pass open — so each one built a full shared-geometry
        // snapshot (a deep document clone, a full cascade and a full layout) and tore it down
        // again, and each teardown also dropped the computed-props memo that the very next
        // element's ancestor walk needed. One elementFromPoint therefore cost O(elements)
        // document layouts instead of one.
        //
        // This is the third instance of the same bug class — WPT #1113 (the check-layout
        // evaluator) and #1115 (the live geometry getters) were the first two, guarded by
        // MulticolCheckLayoutTimeoutTests and LiveGeometryQueryTimeoutTests. Hit testing was the
        // remaining tree-walking geometry consumer that never opened a pass of its own.
        //
        // Sound for the same reason those are: elementFromPoint/elementsFromPoint is a single
        // synchronous DOM query, so no script runs and layout cannot change while the walk is in
        // flight — exactly the static-snapshot precondition WithLayoutGeometryCache documents.
        // Nested calls already share the outermost pass, so every inner read now reuses this one
        // snapshot. Wrapping the private method covers both public entries (ISubDocumentHost and
        // IHitTestHost), which delegate here.
        return WithLayoutGeometryCache<IReadOnlyList<DomElement>>(() =>
        {
            var hits = new List<DomElement>();
            CollectHitTestMatches(documentElement, x, y, hits);
            return hits;
        });
    }

    private void CollectHitTestMatches(DomElement element, double x, double y, List<DomElement> hits)
    {
        for (var i = element.ChildNodes.Count - 1; i >= 0; i--)
        {
            // Only element children are hit-test candidates (skip text/comment).
            if (ChildAt(element, i) is DomElement child && !child.TagName.StartsWith('#'))
                CollectHitTestMatches(child, x, y, hits);
        }

        if (IsElementHitTestCandidate(element, x, y))
            hits.Add(element);
    }


    private bool IsElementHitTestCandidate(DomElement element, double x, double y)
    {
        if (IsAreaElement(element))
            return IsImageMapAreaHit(element, x, y);

        if (!IsElementRenderedForHitTesting(element))
            return false;

        if (IsTableStructuralHitTestOnlyElement(element))
            return false;

        var props = GetComputedProps(element);
        if (string.Equals(props.GetValueOrDefault("pointer-events"), "none", StringComparison.OrdinalIgnoreCase))
            return false;

        var rect = GetHitTestRectForElement(element);
        if (rect.Width <= 0 || rect.Height <= 0)
            return false;

        if (!IsPointInsideRoundedHitRect(element, rect, x, y))
            return false;

        return x >= rect.Left && x < rect.Left + rect.Width &&
               y >= rect.Top && y < rect.Top + rect.Height;
    }

    private (double Left, double Top, double Width, double Height) GetHitTestRectForElement(DomElement element)
    {
        if (IsDocumentElement(element))
            return GetBoundingClientRectForDomElement(element, isRoot: true);

        if (TryGetListItemMarkerHitTestRect(element, out var listItemRect))
            return listItemRect;

        // An SVG shape's rect comes from its own geometry attributes, not from the CSS box tree it
        // is not in — see LayoutMetrics.Svg.cs. Without this every shape measured 0×0, the
        // empty-rect guard in IsElementHitTestCandidate dropped it, and a group's union-of-children
        // had nothing to union, so hit testing stopped at the <svg> root.
        if (TryGetSvgClientRect(element, out var svgRect))
            return svgRect;

        if (IsSvgTextContentElement(element) &&
            TryGetSvgTextHitTestRect(element, out var svgTextRect))
        {
            return svgTextRect;
        }

        if (IsSvgTextContentElement(element) &&
            TryGetSvgChildrenUnionRect(element, out var svgTextChildrenRect))
        {
            return svgTextChildrenRect;
        }

        var rect = GetBoundingClientRectForDomElement(element, isRoot: false);
        return rect;
    }

    private bool TryGetListItemMarkerHitTestRect(DomElement element,
        out (double Left, double Top, double Width, double Height) rect)
    {
        rect = default;
        var props = GetComputedProps(element);
        if (!IsOutsideListItemMarkerCandidate(element, props))
            return false;

        var (Left, Top, Width, Height) = GetBoundingClientRectForDomElement(element, isRoot: false);
        if (Width <= 0 || Height <= 0)
            return false;

        var markerExtent = EstimateOutsideListMarkerExtent(element, props);
        if (markerExtent <= 0)
            return false;

        var isVertical = CssWritingMode.IsVertical(props.GetValueOrDefault("writing-mode"));
        if (isVertical)
        {
            rect = (Left, Top - markerExtent, Width, Height + markerExtent);
            return true;
        }

        var isRtl = string.Equals(props.GetValueOrDefault("direction"), "rtl", StringComparison.OrdinalIgnoreCase);
        rect = isRtl ? (Left, Top, Width + markerExtent, Height)
            : (Left - markerExtent, Top, Width + markerExtent, Height);
        return true;
    }

    private double EstimateOutsideListMarkerExtent(DomElement element, IReadOnlyDictionary<string, string> props)
    {
        var fontSize = Math.Max(8, ResolveFontSizeForElement(element));
        var listStyleType = props.GetValueOrDefault("list-style-type");
        var listStyleImage = props.GetValueOrDefault("list-style-image");
        var markerCore = !string.IsNullOrWhiteSpace(listStyleImage) &&
                         !string.Equals(listStyleImage, "none", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(16, fontSize)
            : string.Equals(listStyleType, "decimal", StringComparison.OrdinalIgnoreCase)
                ? fontSize * 2
                : fontSize;

        return Math.Min(40, markerCore + 8);
    }

    private bool IsImageMapAreaHit(DomElement area, double x, double y)
    {
        var image = FindAssociatedImageMapImage(area);
        if (image == null)
            return false;

        var imageRect = GetBoundingClientRectForDomElement(image, isRoot: false);
        if (imageRect.Width <= 0 || imageRect.Height <= 0)
            return false;

        var localX = x - imageRect.Left;
        var localY = y - imageRect.Top;
        if (localX < 0 || localY < 0 || localX >= imageRect.Width || localY >= imageRect.Height)
            return false;

        var (ScaleX, ScaleY) = GetImageMapCoordinateScale(image, imageRect);
        var scaledX = ScaleX > 0 ? localX / ScaleX : localX;
        var scaledY = ScaleY > 0 ? localY / ScaleY : localY;
        return IsPointInsideAreaShape(area, scaledX, scaledY);
    }

    private DomElement? FindAssociatedImageMapImage(DomElement area)
    {
        var map = ParentEl(area);
        if (map == null || !string.Equals(map.TagName, "map", StringComparison.OrdinalIgnoreCase))
            return null;

        var mapName = GetAttr(map, "name");
        if (string.IsNullOrWhiteSpace(mapName))
            mapName = GetAttr(map, "id");
        if (string.IsNullOrWhiteSpace(mapName))
            return null;

        var expectedUseMap = "#" + mapName.Trim();
        foreach (var candidate in EnumerateDomDescendants(GetOwningDocumentElement(area)))
        {
            if (!string.Equals(candidate.TagName, "img", StringComparison.OrdinalIgnoreCase))
                continue;

            var useMap = GetAttr(candidate, "usemap")?.Trim();
            if (string.Equals(useMap, expectedUseMap, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return null;
    }

    private IEnumerable<DomElement> EnumerateDomDescendants(DomElement root)
    {
        foreach (var child in ChildElements(root))
        {
            if (child.TagName.StartsWith('#'))
                continue;

            yield return child;
            foreach (var descendant in EnumerateDomDescendants(child))
                yield return descendant;
        }
    }

    private (double ScaleX, double ScaleY) GetImageMapCoordinateScale(
        DomElement image,
        (double Left, double Top, double Width, double Height) imageRect)
    {
        var widthBasis = ParsePositiveDouble(GetAttr(image, "width"));
        var heightBasis = ParsePositiveDouble(GetAttr(image, "height"));

        return (
            widthBasis > 0 ? imageRect.Width / widthBasis : 1,
            heightBasis > 0 ? imageRect.Height / heightBasis : 1);
    }

    private bool IsPointInsideAreaShape(DomElement area, double x, double y)
    {
        var shape = GetAttr(area, "shape")?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(shape))
            shape = "rect";

        var coords = ParseAreaCoords(GetAttr(area, "coords"));
        return shape switch
        {
            "default" => true,
            "circle" => coords.Count >= 3 && IsPointInsideCircleArea(coords, x, y),
            "poly" or "polygon" => coords.Count >= 6 && IsPointInsidePolygonArea(coords, x, y),
            _ => coords.Count >= 4 && IsPointInsideRectArea(coords, x, y)
        };
    }

    private bool IsPointInsideRoundedHitRect(DomElement element,
        (double Left, double Top, double Width, double Height) rect,
        double x, double y)
    {
        var radius = GetUniformHitTestBorderRadius(element, rect.Width, rect.Height);
        if (radius <= 0)
            return true;

        var rx = Math.Min(radius, rect.Width / 2);
        var ry = Math.Min(radius, rect.Height / 2);
        if (rx <= 0 || ry <= 0)
            return true;

        var localX = x - rect.Left;
        var localY = y - rect.Top;
        if (localX < 0 || localY < 0 || localX >= rect.Width || localY >= rect.Height)
            return false;

        return !IsPointInsideRoundedCorner(localX, localY, rx, ry, rect.Width, rect.Height, top: true, left: true) &&
               !IsPointInsideRoundedCorner(localX, localY, rx, ry, rect.Width, rect.Height, top: true, left: false) &&
               !IsPointInsideRoundedCorner(localX, localY, rx, ry, rect.Width, rect.Height, top: false, left: true) &&
               !IsPointInsideRoundedCorner(localX, localY, rx, ry, rect.Width, rect.Height, top: false, left: false);
    }

    private bool IsPointInsideRoundedCorner(double localX, double localY,
        double rx, double ry, double width, double height, bool top, bool left)
    {
        var cornerLeft = left ? 0 : width - rx;
        var cornerTop = top ? 0 : height - ry;
        if (localX < cornerLeft || localX >= cornerLeft + rx || localY < cornerTop || localY >= cornerTop + ry)
            return false;

        var centerX = left ? rx : width - rx;
        var centerY = top ? ry : height - ry;
        var normalizedX = (localX - centerX) / rx;
        var normalizedY = (localY - centerY) / ry;
        return normalizedX * normalizedX + normalizedY * normalizedY > 1;
    }

    private double GetUniformHitTestBorderRadius(DomElement element, double width, double height)
    {
        var rawRadius = GetComputedProps(element).GetValueOrDefault("border-radius");
        if (string.IsNullOrWhiteSpace(rawRadius) || string.Equals(rawRadius, "0", StringComparison.Ordinal))
            return 0;

        var firstToken = rawRadius
            .Split([' ', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstToken))
            return 0;

        return ParseCssLengthToPixelsWithViewport(firstToken, element, percentageBasis: Math.Min(width, height));
    }

    private bool IsElementRenderedForHitTesting(DomElement element)
    {
        for (var current = element; current != null; current = ParentEl(current))
        {
            if (current.TagName.StartsWith('#'))
                continue;

            var props = GetComputedProps(current);
            var display = props.GetValueOrDefault("display");
            if (string.Equals(display, "none", StringComparison.OrdinalIgnoreCase))
                return false;

            var visibility = props.GetValueOrDefault("visibility");
            if (string.Equals(visibility, "hidden", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(visibility, "collapse", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private bool DocumentHasViewport(DomElement documentElement)
    {
        var docRoot = GetOwningDocument(documentElement);
        // The owning document comes from the canonical tree, not OwnerDocRoot.
        // The main document and rendered nested browsing contexts (iframe/object/frame — reachable
        // through a container frame in the content-document map) have a viewport. This replaces the
        // former heuristic that relied on regime-A iframe nodes carrying a null OwnerDocRoot: those
        // content documents share the CreateBrowsingContextDocument HasViewport=false marker with
        // detached programmatic documents, so the frame-container test is what distinguishes them.
        if (ReferenceEquals(docRoot, _document) || GetFrameForContentDocument(docRoot) != null)
            return true;

        // A detached programmatic document (createDocument/createHTMLDocument) is viewport-less.
        return !DocumentStateFor(docRoot).HasViewport.TryGet(out var value) ||
               value is not bool hasViewport ||
               hasViewport;
    }
}

public sealed partial class DomBridge
{
    /// <summary>
    /// One <c>check-layout-th.js</c> geometry assertion evaluated against the
    /// bridge's computed box metrics: the <c>data-*</c> attribute's expected
    /// value alongside the value the bridge computes for the same element.
    /// </summary>
    /// <param name="Element">Human-readable element descriptor (e.g. <c>span.abspos[title=start]</c>).</param>
    /// <param name="Property">The checked geometry property (e.g. <c>offset-y</c>, <c>width</c>).</param>
    /// <param name="Expected">The value declared by the test's <c>data-*</c> attribute.</param>
    /// <param name="Actual">The value the bridge's layout-metrics estimator computes.</param>
    public readonly record struct CheckLayoutAssertion(string Element, string Property, double Expected, double Actual);

    /// <summary>
    /// Evaluates the <c>data-offset-*</c> / <c>data-expected-*</c> /
    /// <c>data-total-*</c> assertions that <c>check-layout-th.js</c> tests carry,
    /// comparing each against the bridge's computed box geometry — the same metrics
    /// (<c>offsetTop</c>, <c>offsetWidth</c>, …) the test's own JavaScript would read.
    /// Returns one entry per declared assertion (the caller decides pass/fail with a
    /// tolerance). The runner uses this to turn an opaque pixel mismatch on a
    /// check-layout test into a precise, font-independent "<c>expected offset-y=15,
    /// got 0</c>" diagnostic.
    /// </summary>
    public IReadOnlyList<CheckLayoutAssertion> EvaluateCheckLayoutAssertions()
    {
        // The whole pass reads one static post-script layout snapshot, so the
        // box-geometry estimators can be memoized for its duration — without this
        // a deep tree's offset queries are exponential and time out (WPT #1113).
        return WithLayoutGeometryCache(() =>
        {
            var results = new List<CheckLayoutAssertion>();
            if (DocumentElement is { } root)
                CollectCheckLayoutAssertions(root, results);
            return results;
        });
    }

    private void CollectCheckLayoutAssertions(Broiler.Dom.DomElement element, List<CheckLayoutAssertion> results)
    {
        foreach (var (attribute, property) in CheckLayoutAttributeMap)
        {
            if (TryGetAttribute(element, attribute, out var raw) && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var expected))
            {
                results.Add(new CheckLayoutAssertion(DescribeElement(element), property, expected, ComputeCheckLayoutMetric(element, property)));
            }
        }

        foreach (var child in ChildElements(element))
            CollectCheckLayoutAssertions(child, results);
    }

    private double ComputeCheckLayoutMetric(Broiler.Dom.DomElement element, string property)
    {
        var isRoot = IsViewportElementForMetrics(element);
        return property switch
        {
            "offset-x" => GetOffsetLeftForDomElement(element),
            "offset-y" => GetOffsetTopForDomElement(element),
            "width" => GetOffsetWidthForDomElement(element, isRoot),
            "height" => GetOffsetHeightForDomElement(element, isRoot),
            "client-width" => GetClientWidthForDomElement(element, isRoot),
            "client-height" => GetClientHeightForDomElement(element, isRoot),
            // check-layout's data-total-{x,y} is offset + border-box size on that axis.
            "total-x" => GetOffsetLeftForDomElement(element) + GetOffsetWidthForDomElement(element, isRoot),
            "total-y" => GetOffsetTopForDomElement(element) + GetOffsetHeightForDomElement(element, isRoot),
            _ => double.NaN,
        };
    }
}
