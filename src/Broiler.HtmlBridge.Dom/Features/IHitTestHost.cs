using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="HitTestBinding"/> needs from the bridge: the document root, the
/// JS-wrapper factory, and the point hit-test that returns the front-to-back stack of elements at a
/// document coordinate. Coordinate parsing is the bridge's neutral <c>internal static</c>
/// <c>GetCoordinateArgument</c> helper, called directly.
/// </summary>
internal interface IHitTestHost
{
    DomElement DocumentElement { get; }
    JSObject ToJSObject(DomNode node);
    IReadOnlyList<DomElement> HitTestDocumentPoint(DomNode docRoot, double x, double y);
}
