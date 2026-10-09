using Broiler.Graphics.Color;
using Broiler.JSeal;

namespace Broiler.HtmlBridge;

// Canvas gradients interpolate between two circles, not a CSS elliptical gradient tile.
internal sealed class CanvasGradientPaint(double x0, double y0, double r0, double x1, double y1, double r1, bool radial)
{
    internal JsValue Handle { get; set; }
    private readonly List<(double Offset, BColor Color)> _stops = [];

    internal void Add(double offset, BColor color)
    {
        var index = _stops.FindIndex(stop => stop.Offset > offset);
        if (index < 0) _stops.Add((offset, color)); else _stops.Insert(index, (offset, color));
    }

    internal BColor Sample(double x, double y)
    {
        if (_stops.Count == 0) return BColor.Transparent;
        var dx = x1 - x0; var dy = y1 - y0;
        var px = x - x0; var py = y - y0;
        double t;
        if (!radial)
        {
            var length = dx * dx + dy * dy;
            if (length == 0) return BColor.Transparent;
            t = (px * dx + py * dy) / length;
        }
        else
        {
            var dr = r1 - r0;
            var a = dx * dx + dy * dy - dr * dr;
            var b = -2 * (px * dx + py * dy + r0 * dr);
            var c = px * px + py * py - r0 * r0;
            if (a == 0 && b == 0) return BColor.Transparent;
            if (Math.Abs(a) < 1e-12) t = -c / b;
            else
            {
                var discriminant = b * b - 4 * a * c;
                if (discriminant < 0) return BColor.Transparent;
                var root = Math.Sqrt(discriminant);
                var t0 = (-b - root) / (2 * a); var t1 = (-b + root) / (2 * a);
                t = double.NegativeInfinity;
                if (r0 + t0 * dr >= 0) t = t0;
                if (r0 + t1 * dr >= 0) t = Math.Max(t, t1);
            }
            if (!double.IsFinite(t) || r0 + t * dr < 0) return BColor.Transparent;
        }
        if (t < _stops[0].Offset) return _stops[0].Color;
        for (var i = 1; i < _stops.Count; i++)
        {
            if (t >= _stops[i].Offset) continue;
            var left = _stops[i - 1]; var right = _stops[i];
            var ratio = (t - left.Offset) / (right.Offset - left.Offset);
            byte Mix(byte a, byte b) => (byte)Math.Clamp(Math.Round(a + (b - a) * ratio), 0, 255);
            return new BColor(Mix(left.Color.R, right.Color.R), Mix(left.Color.G, right.Color.G),
                Mix(left.Color.B, right.Color.B), Mix(left.Color.A, right.Color.A));
        }
        return _stops[^1].Color;
    }
}
