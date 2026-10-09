using System.Drawing;
using Broiler.Graphics.Color;
using Broiler.Graphics.Imaging;
using Broiler.Graphics.Rendering;

namespace Broiler.HtmlBridge;

internal sealed partial class CanvasRenderingContext2D
{
    internal CanvasGradientPaint? FillGradient { get; set; }
    internal CanvasGradientPaint? StrokeGradient { get; set; }

    public void Ellipse(double x, double y, double rx, double ry, double rotation,
        double start, double end, bool anticlockwise)
    {
        var sweep = end - start;
        if (!anticlockwise)
            sweep = sweep >= Math.Tau ? Math.Tau : (sweep % Math.Tau + Math.Tau) % Math.Tau;
        else
            sweep = -sweep >= Math.Tau ? -Math.Tau : -((-sweep % Math.Tau + Math.Tau) % Math.Tau);
        var steps = Math.Max(1, FlatteningSteps((float)Math.Max(rx, ry), Math.Abs(sweep)));
        var path = CurrentSubpath();
        var cos = Math.Cos(rotation);
        var sin = Math.Sin(rotation);
        for (var i = 0; i <= steps; i++)
        {
            var angle = start + sweep * i / steps;
            var px = rx * Math.Cos(angle);
            var py = ry * Math.Sin(angle);
            path.Add(TransformPoint(x + px * cos - py * sin, y + px * sin + py * cos));
        }
    }

    public void Bezier(double x1, double y1, double x2, double y2, double x, double y)
    {
        var path = CurrentSubpath();
        if (path.Count == 0) path.Add(TransformPoint(x1, y1));
        Flatten(path[^1], TransformPoint(x1, y1), TransformPoint(x2, y2), TransformPoint(x, y), 0);
        void Flatten(PointF a, PointF b, PointF c, PointF d, int depth)
        {
            // De Casteljau subdivision, bounded even for hostile coordinates.
            var ux = 3 * b.X - 2 * a.X - d.X; var uy = 3 * b.Y - 2 * a.Y - d.Y;
            var vx = 3 * c.X - 2 * d.X - a.X; var vy = 3 * c.Y - 2 * d.Y - a.Y;
            if (depth >= 12 || Math.Max(ux * ux, vx * vx) + Math.Max(uy * uy, vy * vy) <= 0.25)
            { path.Add(d); return; }
            static PointF Mid(PointF p, PointF q) => new((p.X + q.X) / 2, (p.Y + q.Y) / 2);
            var ab = Mid(a, b); var bc = Mid(b, c); var cd = Mid(c, d);
            var abc = Mid(ab, bc); var bcd = Mid(bc, cd); var mid = Mid(abc, bcd);
            Flatten(a, ab, abc, mid, depth + 1); Flatten(mid, bcd, cd, d, depth + 1);
        }
    }

    public void Quadratic(double cx, double cy, double x, double y)
    {
        var path = CurrentSubpath();
        if (path.Count == 0) path.Add(TransformPoint(cx, cy));
        if (!InversePoint(path[^1].X, path[^1].Y, out var p)) return;
        Bezier(p.X + (cx - p.X) * 2 / 3, p.Y + (cy - p.Y) * 2 / 3,
            x + (cx - x) * 2 / 3, y + (cy - y) * 2 / 3, x, y);
    }

    private void Paint(Action<BCanvas, BColor> operation, string style, CanvasGradientPaint? gradient)
    {
        if (gradient is null) { Draw(canvas => operation(canvas, ResolveColor(style))); return; }
        if (_bitmap is null) return;
        using var mask = new BBitmap(Width, Height);
        using (var canvas = mask.OpenCanvas()) operation(canvas, new BColor(255, 255, 255));
        Colorize(mask, gradient);
        Draw(canvas => canvas.DrawBitmap(mask, new RectangleF(0, 0, Width, Height), new RectangleF(0, 0, Width, Height)));
    }

    private void Colorize(BBitmap mask, CanvasGradientPaint gradient)
    {
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                var coverage = mask.GetPixel(x, y).A;
                if (coverage == 0) continue;
                var color = InversePoint(x + 0.5, y + 0.5, out var local)
                    ? gradient.Sample(local.X, local.Y) : BColor.Transparent;
                mask.SetPixel(x, y, new BColor(color.R, color.G, color.B,
                    (byte)Math.Clamp(Math.Round(color.A * coverage / 255.0 * GlobalAlpha), 0, 255)));
            }
    }
}
