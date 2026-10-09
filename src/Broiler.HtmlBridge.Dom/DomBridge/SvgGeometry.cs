using Broiler.Dom;
using Broiler.Graphics.Text;
using Broiler.HtmlBridge.Dom.Features;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

public sealed partial class DomBridge : ISvgGeometryHost
{
    SvgTextMeasurement ISvgGeometryHost.SvgText(DomElement element)
    {
        var props = GetComputedProps(element);
        var size = ResolveFontSizeForElement(element);
        if (size <= 0 || !double.IsFinite(size)) size = 16;
        var family = props.GetValueOrDefault("font-family") ?? "sans-serif";
        var shorthand = $"{props.GetValueOrDefault("font-style") ?? "normal"} {props.GetValueOrDefault("font-weight") ?? "normal"} {size.ToString(System.Globalization.CultureInfo.InvariantCulture)}px {family}";
        if (!CanvasFont.TryResolve(shorthand, out var font)) font = CanvasFont.Default;
        var text = CanvasFont.PrepareText(element.TextContent);
        var metrics = BTextMeasurer.Measure(text, font);
        var x = ResolveSvgTextCoordinate(element, "x");
        var y = ResolveSvgTextCoordinate(element, "y");
        if (props.GetValueOrDefault("text-anchor") == "middle") x -= metrics.Advance / 2;
        else if (props.GetValueOrDefault("text-anchor") == "end") x -= metrics.Advance;
        return new SvgTextMeasurement(text, x, y, metrics.Baseline, metrics.Size.Height,
            value => BTextMeasurer.MeasureAdvance(value, font));
    }

    (double X, double Y, double Width, double Height) ISvgGeometryHost.SvgBounds(DomElement element)
    {
        if (IsSvgTextContentElement(element))
        {
            var text = ((ISvgGeometryHost)this).SvgText(element);
            return (text.X, text.Y - text.Baseline, text.Measure(text.Text), text.Height);
        }
        var viewport = FindNearestSvgViewportAncestor(element) ?? element;
        if (TryGetSvgUserSpaceBounds(element, viewport, out var bounds)) return bounds;
        (double X, double Y, double Width, double Height)? union = null;
        foreach (var child in ChildElements(element))
        {
            if (!IsSvgGeometryElement(child) && !IsSvgTextContentElement(child) && !IsSvgGroupElement(child)) continue;
            var r = ((ISvgGeometryHost)this).SvgBounds(child);
            if (r.Width == 0 && r.Height == 0) continue;
            // Existing SVG geometry supports translate on groups; preserve that here in user space.
            var (dx, dy) = AccumulatedSvgTranslate(child, element);
            r.X += dx; r.Y += dy;
            if (union is not { } u) union = r;
            else
            {
                var x = Math.Min(u.X, r.X); var y = Math.Min(u.Y, r.Y);
                union = (x, y, Math.Max(u.X + u.Width, r.X + r.Width) - x,
                    Math.Max(u.Y + u.Height, r.Y + r.Height) - y);
            }
        }
        return union ?? (0, 0, 0, 0);
    }
}
