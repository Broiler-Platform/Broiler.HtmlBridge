using System.Linq;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>document</c> point hit-testing methods — <c>document.elementFromPoint</c>,
/// <c>document.elementsFromPoint</c> — co-located as an HtmlBridge feature module (Phase 3).
/// <c>elementFromPoint</c> returns the topmost element at a document coordinate (or <c>null</c>);
/// <c>elementsFromPoint</c> returns the whole front-to-back stack. The realm, the document root, the
/// wrapper factory and the point hit-test are reached through the narrow <see cref="IHitTestHost"/>
/// contract. Previously the bridge's <c>JsRegistrationElementFromPoint011Core</c>/
/// <c>ElementsFromPoint012Core</c> in the shared JsFunctionCallbacks/Registration.cs grab-bag.
/// </summary>
/// <remarks>
/// Everything here is JSEAL's, the call frame included: <c>DomBridge/Registration/Document.cs</c> mints
/// both through the realm. <see cref="Coordinate"/> is the bridge's former
/// <c>DomBridge.GetCoordinateArgument</c>, moved into its only caller so that
/// <c>DomBridge/HitTesting.cs</c> — 582 lines of pure geometry — no longer names an engine type for the
/// sake of one argument read. It coerces through the realm because <c>elementFromPoint("10", "20")</c>
/// is a page passing strings, which is what the engine's <c>DoubleValue</c> did here before.
/// </remarks>
internal static class HitTestBinding
{
    public static JsValue ElementFromPoint(IHitTestHost host, in JsCall call)
    {
        var hit = host.HitTestDocumentPoint(
            host.DocumentElement, Coordinate(in call, 0), Coordinate(in call, 1)).FirstOrDefault();
        return hit != null ? host.WrapNode(hit) : JsValue.Null;
    }

    public static JsValue ElementsFromPoint(IHitTestHost host, in JsCall call)
    {
        var hits = host.HitTestDocumentPoint(
            host.DocumentElement, Coordinate(in call, 0), Coordinate(in call, 1));
        return host.Realm.NewArray([.. hits.Select(host.WrapNode)]);
    }

    /// <summary>
    /// One of <c>elementFromPoint</c>'s two coordinates: the number the page passed, and NaN when it
    /// passed nothing, <c>null</c> or <c>undefined</c> — which the hit test reads as "no such point"
    /// and answers with an empty stack.
    /// </summary>
    private static double Coordinate(in JsCall call, int index) =>
        call[index].IsNullish ? double.NaN : call.Realm.ToNumber(call[index]);
}
