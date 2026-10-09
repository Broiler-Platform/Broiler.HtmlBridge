using System.Drawing;
using Broiler.Graphics.Color;
using Broiler.Graphics.Geometry;
using Broiler.Graphics.Imaging;
using Broiler.Graphics.Rendering;
using Broiler.Graphics.RenderList;

namespace Broiler.HtmlBridge;

internal sealed partial class CanvasRenderingContext2D
{
    internal BMatrix3x2 Transform { get; set; } = BMatrix3x2.Identity;
    internal void Concatenate(BMatrix3x2 matrix) => Transform = matrix * Transform;

    private PointF TransformPoint(double x, double y)
    {
        var point = Transform.Transform(new BPoint(x, y));
        return new PointF((float)point.X, (float)point.Y);
    }

    private PointF[] RectPoints(double x, double y, double w, double h, bool close = false)
    {
        var points = new[] { TransformPoint(x, y), TransformPoint(x + w, y),
            TransformPoint(x + w, y + h), TransformPoint(x, y + h) };
        return close ? [.. points, points[0]] : points;
    }

    private bool InversePoint(double x, double y, out PointF point)
    {
        var m = Transform;
        var determinant = m.M11 * m.M22 - m.M12 * m.M21;
        point = default;
        if (determinant == 0 || !double.IsFinite(determinant)) return false;
        x -= m.M31; y -= m.M32;
        point = new((float)((x * m.M22 - y * m.M21) / determinant),
            (float)((y * m.M11 - x * m.M12) / determinant));
        return true;
    }

    private void DrawTransformedList(BRenderList list, CanvasGradientPaint? gradient)
    {
        if (_bitmap is null) return;
        using var renderer = new BImageRenderer();
        using BBitmap pixels = renderer.RenderToImage(list,
            new BSurfaceDescriptor(new BSize(Width, Height), 1), new BFrameContext(BColor.Transparent));
        if (gradient is not null) Colorize(pixels, gradient);
        Draw(canvas => canvas.DrawBitmap(pixels, new RectangleF(0, 0, Width, Height), new RectangleF(0, 0, Width, Height)));
    }

    private void DrawTransformedImage(BBitmap source, RectangleF destination, RectangleF sourceRect)
    {
        if (_bitmap is null || destination.Width <= 0 || destination.Height <= 0) return;
        using var pixels = new BBitmap(Width, Height);
        for (var y = 0; y < Height; y++)
            for (var x = 0; x < Width; x++)
            {
                if (!InversePoint(x + 0.5, y + 0.5, out var local) || !destination.Contains(local)) continue;
                var sx = (int)Math.Floor(sourceRect.X + (local.X - destination.X) / destination.Width * sourceRect.Width);
                var sy = (int)Math.Floor(sourceRect.Y + (local.Y - destination.Y) / destination.Height * sourceRect.Height);
                if (sx >= 0 && sx < source.Width && sy >= 0 && sy < source.Height)
                    pixels.SetPixel(x, y, source.GetPixel(sx, sy));
            }
        Draw(canvas => canvas.DrawBitmap(pixels, new RectangleF(0, 0, Width, Height), new RectangleF(0, 0, Width, Height)));
    }
}
