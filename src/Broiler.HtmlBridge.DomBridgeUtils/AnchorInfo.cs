using Broiler.Dom;

namespace Broiler.HtmlBridge;

internal sealed record AnchorInfo(double Top, double Left, double Width, double Height, DomElement? SourceElement = null)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
}
