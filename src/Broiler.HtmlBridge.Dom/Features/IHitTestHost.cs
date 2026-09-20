using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="HitTestBinding"/> needs from the bridge: the realm the element
/// stack is minted in, the document root, the JS-wrapper factory, and the point hit-test that returns the
/// front-to-back stack of elements at a document coordinate.
/// </summary>
/// <remarks>
/// The contract names no engine type, and that includes the wrapper factory's own name
/// (<see cref="WrapNode"/>). See the remarks on <see cref="ITableHost"/> for why a member name counts.
/// </remarks>
internal interface IHitTestHost
{
    /// <summary>The realm the <c>elementsFromPoint</c> array is minted in.</summary>
    IJsRealm Realm { get; }

    DomElement DocumentElement { get; }

    /// <summary>The single JS wrapper identity for <paramref name="node"/>.</summary>
    JsValue WrapNode(DomNode node);

    IReadOnlyList<DomElement> HitTestDocumentPoint(DomNode docRoot, double x, double y);
}
