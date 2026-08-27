using System;
using System.Globalization;
using Broiler.Dom;

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
    /// <summary>A CSS 2D affine transform <c>matrix(a,b,c,d,e,f)</c> mapping a point
    /// <c>(x,y) → (a·x + c·y + e, b·x + d·y + f)</c>.</summary>
    private readonly record struct Affine(double A, double B, double C, double D, double E, double F)
    {
        public static readonly Affine Identity = new(1, 0, 0, 1, 0, 0);

        public bool IsIdentity =>
            A == 1 && B == 0 && C == 0 && D == 1 && E == 0 && F == 0;

        /// <summary>The matrix that applies <c>this</c> first and then <paramref name="next"/>.</summary>
        public Affine Then(Affine next) => new(
            next.A * A + next.C * B,
            next.B * A + next.D * B,
            next.A * C + next.C * D,
            next.B * C + next.D * D,
            next.A * E + next.C * F + next.E,
            next.B * E + next.D * F + next.F);
    }

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

    /// <summary>
    /// CSS Transforms 1 §8: the transform origin, as an offset from the box's own top-left corner.
    /// </summary>
    /// <remarks>
    /// Defers to <see cref="Layout.IR.CssTransformOrigin"/>, the grammar shared with the SVG
    /// renderer and the paint walker. Reading it here on its own got three things wrong that only
    /// the shared reading has ever handled: a lone <c>top</c> or <c>bottom</c> names the
    /// <em>vertical</em> axis and centres the other, so taking the first component as x put it on
    /// the wrong one; the keyword pair may be written <c>top left</c>, which has to be swapped back;
    /// and an invalid declaration such as <c>top 100%</c> — a length-percentage may not follow a
    /// vertical keyword — is dropped whole rather than half-read.
    /// </remarks>
    private static (double X, double Y) ParseTransformOrigin(string? origin, double width, double height)
    {
        var point = Layout.IR.CssTransformOrigin.Resolve(
            origin,
            new System.Drawing.RectangleF(0, 0, (float)width, (float)height),
            initialIsBoxCorner: false);

        return (point.X, point.Y);
    }

    /// <summary>Composes a CSS <c>transform</c> function list into a single affine matrix. Functions
    /// apply left-to-right as outermost-to-innermost (CSS Transforms §1), so the list is folded in
    /// reverse. Unrecognised or 3D functions contribute identity — a conservative no-op that leaves
    /// the geometry untransformed rather than wrong.</summary>
    private static Affine ParseTransformFunctions(string transform, double boxWidth, double boxHeight)
    {
        var functions = SplitTransformFunctions(transform);
        var matrix = Affine.Identity;
        // Fold in reverse so the first-listed function ends up outermost (applied last to a point).
        for (var i = functions.Count - 1; i >= 0; i--)
        {
            var f = ParseTransformFunction(functions[i].Name, functions[i].Args, boxWidth, boxHeight);
            matrix = matrix.Then(f);
        }
        return matrix;
    }

    private static Affine ParseTransformFunction(string name, string args, double boxWidth, double boxHeight)
    {
        var values = args.Split(',');

        double Number(int i) =>
            i < values.Length && double.TryParse(values[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;

        double Length(int i, double percentBasis) =>
            i < values.Length ? ResolveLength(values[i].Trim(), percentBasis) : 0;

        // A scale factor is <number> | <percentage> (css-transforms-2 §"scale"), and the percentage
        // is simply the ratio: scale(50%) is scale(0.5). Number() could not parse "50%" at all and
        // fell back to 0, which collapsed the box instead of halving it. Absent or unparsable falls
        // back to 1, matching this parser's rule that what it cannot model contributes identity.
        double Ratio(int i)
        {
            if (i >= values.Length)
                return 1;

            var v = values[i].Trim();
            if (v.EndsWith('%'))
                return double.TryParse(v[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)
                    ? pct / 100.0
                    : 1;

            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 1;
        }

        switch (name)
        {
            case "matrix":
                return values.Length >= 6
                    ? new Affine(Number(0), Number(1), Number(2), Number(3), Number(4), Number(5))
                    : Affine.Identity;
            case "scale":
            {
                var sx = Ratio(0);
                var sy = values.Length >= 2 ? Ratio(1) : sx;
                return new Affine(sx, 0, 0, sy, 0, 0);
            }
            case "scalex":
                return new Affine(Ratio(0), 0, 0, 1, 0, 0);
            case "scaley":
                return new Affine(1, 0, 0, Ratio(0), 0, 0);
            case "translate":
                return new Affine(1, 0, 0, 1, Length(0, boxWidth), Length(1, boxHeight));
            case "translatex":
                return new Affine(1, 0, 0, 1, Length(0, boxWidth), 0);
            case "translatey":
                return new Affine(1, 0, 0, 1, 0, Length(0, boxHeight));
            case "rotate":
            case "rotatez":
            {
                var radians = ParseAngleRadians(values.Length > 0 ? values[0].Trim() : "0");
                var cos = Math.Cos(radians);
                var sin = Math.Sin(radians);
                return new Affine(cos, sin, -sin, cos, 0, 0);
            }
            case "skewx":
                return new Affine(1, 0, Math.Tan(ParseAngleRadians(values.Length > 0 ? values[0].Trim() : "0")), 1, 0, 0);
            case "skewy":
                return new Affine(1, Math.Tan(ParseAngleRadians(values.Length > 0 ? values[0].Trim() : "0")), 0, 1, 0, 0);
            default:
                // translate3d/scale3d/matrix3d/perspective/etc. — not modelled here; leave identity.
                return Affine.Identity;
        }
    }

    private static double ResolveLength(string value, double percentBasis)
    {
        if (value.EndsWith("%", StringComparison.Ordinal) &&
            double.TryParse(value[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            return percentBasis * pct / 100.0;
        return ParsePixels(value) ?? 0;
    }

    private static double? ParsePixels(string value)
    {
        var v = value.Trim();
        if (v.EndsWith("px", StringComparison.OrdinalIgnoreCase))
            v = v[..^2];
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static double ParseAngleRadians(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        if (v.EndsWith("deg", StringComparison.Ordinal))
            return double.TryParse(v[..^3], NumberStyles.Float, CultureInfo.InvariantCulture, out var deg) ? deg * Math.PI / 180.0 : 0;
        if (v.EndsWith("grad", StringComparison.Ordinal))
            return double.TryParse(v[..^4], NumberStyles.Float, CultureInfo.InvariantCulture, out var g) ? g * Math.PI / 200.0 : 0;
        if (v.EndsWith("turn", StringComparison.Ordinal))
            return double.TryParse(v[..^4], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t * 2 * Math.PI : 0;
        if (v.EndsWith("rad", StringComparison.Ordinal))
            return double.TryParse(v[..^3], NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : 0;
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var bare) ? bare * Math.PI / 180.0 : 0;
    }

    /// <summary>Splits a transform value into its <c>name(args)</c> functions, in source order.</summary>
    private static System.Collections.Generic.List<(string Name, string Args)> SplitTransformFunctions(string transform)
    {
        var result = new System.Collections.Generic.List<(string, string)>();
        var position = 0;
        while (position < transform.Length)
        {
            var open = transform.IndexOf('(', position);
            if (open < 0)
                break;
            var close = transform.IndexOf(')', open + 1);
            if (close < 0)
                break;

            var name = transform[position..open].Trim().ToLowerInvariant();
            var args = transform[(open + 1)..close];
            if (name.Length > 0)
                result.Add((name, args));
            position = close + 1;
        }
        return result;
    }
}
