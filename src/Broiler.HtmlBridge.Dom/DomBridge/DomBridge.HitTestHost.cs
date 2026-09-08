using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit IHitTestHost implementation for the HitTestBinding feature module (Phase 3): the bridge
// exposes the realm, the document root, the JS-wrapper factory and the point hit-test via explicit
// interface members, so the module never reaches an arbitrary bridge private field and the public surface
// is unchanged.
//
// Realm is implemented explicitly because DomBridge.Realm is internal: an implicit implementation of a
// public interface member cannot be satisfied by a non-public property (CS0737).
public sealed partial class DomBridge : Dom.Features.IHitTestHost
{
    IJsRealm Dom.Features.IHitTestHost.Realm => Realm;

    DomElement Dom.Features.IHitTestHost.DocumentElement => DocumentElement;

    JsValue Dom.Features.IHitTestHost.WrapNode(DomNode node) => WrapNode(node);

    IReadOnlyList<DomElement> Dom.Features.IHitTestHost.HitTestDocumentPoint(DomNode docRoot, double x, double y)
        => HitTestDocumentPoint(docRoot, x, y);
}
