using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

// Explicit IHitTestHost implementation for the HitTestBinding feature module (Phase 3): the bridge
// exposes the document root, the JS-wrapper factory and the point hit-test via explicit interface
// members, so the module never reaches an arbitrary bridge private field and the public surface is
// unchanged.
public sealed partial class DomBridge : Dom.Features.IHitTestHost
{
    DomElement Dom.Features.IHitTestHost.DocumentElement => DocumentElement;

    JSObject Dom.Features.IHitTestHost.ToJSObject(DomNode node) => ToJSObject(node);

    IReadOnlyList<DomElement> Dom.Features.IHitTestHost.HitTestDocumentPoint(DomNode docRoot, double x, double y)
        => HitTestDocumentPoint(docRoot, x, y);
}
