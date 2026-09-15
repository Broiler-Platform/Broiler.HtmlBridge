using System;
using Broiler.Dom;
using static Broiler.HtmlBridge.DomBridgeUtils;
using static Broiler.HtmlBridge.DomBridgeHostUtils;

namespace Broiler.HtmlBridge;

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
