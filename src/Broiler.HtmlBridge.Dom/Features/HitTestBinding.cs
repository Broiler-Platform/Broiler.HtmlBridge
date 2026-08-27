using System.Linq;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Null;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>document</c> point hit-testing methods — <c>document.elementFromPoint</c>,
/// <c>document.elementsFromPoint</c> — co-located as an HtmlBridge feature module (Phase 3).
/// <c>elementFromPoint</c> returns the topmost element at a document coordinate (or <c>null</c>);
/// <c>elementsFromPoint</c> returns the whole front-to-back stack. The document root, wrapper factory
/// and the point hit-test are reached through the narrow <see cref="IHitTestHost"/> contract;
/// coordinate parsing uses the bridge's neutral <c>internal static</c> <c>GetCoordinateArgument</c>.
/// Previously the bridge's <c>JsRegistrationElementFromPoint011Core</c>/<c>ElementsFromPoint012Core</c>
/// in the shared JsFunctionCallbacks/Registration.cs grab-bag.
/// </summary>
internal static class HitTestBinding
{
    public static JSValue ElementFromPoint(IHitTestHost host, in Arguments a)
    {
        var hit = host.HitTestDocumentPoint(
            host.DocumentElement, DomBridge.GetCoordinateArgument(a, 0), DomBridge.GetCoordinateArgument(a, 1)).FirstOrDefault();
        return hit != null ? host.ToJSObject(hit) : JSNull.Value;
    }

    public static JSValue ElementsFromPoint(IHitTestHost host, in Arguments a)
    {
        var hits = host.HitTestDocumentPoint(
            host.DocumentElement, DomBridge.GetCoordinateArgument(a, 0), DomBridge.GetCoordinateArgument(a, 1));
        return new JSArray([.. hits.Select(host.ToJSObject)]);
    }
}
