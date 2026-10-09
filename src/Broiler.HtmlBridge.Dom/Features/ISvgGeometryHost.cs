using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

internal interface ISvgGeometryHost
{
    (double X, double Y, double Width, double Height) SvgBounds(DomElement element);
    SvgTextMeasurement SvgText(DomElement element);
}

internal sealed record SvgTextMeasurement(string Text, double X, double Y, double Baseline,
    double Height, Func<string, double> Measure);
