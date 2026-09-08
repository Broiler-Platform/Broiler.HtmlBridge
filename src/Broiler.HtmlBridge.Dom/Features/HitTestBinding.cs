using System.Linq;
using Broiler.HtmlBridge.Jseal;

// Engine-typed only for the two entry points below, whose caller is an unmigrated registration site with
// an engine call frame: DomBridge/Registration/Document.cs installs document.elementFromPoint and
// document.elementsFromPoint as JSFunctions and hands each an `in Arguments`.
using Broiler.JavaScript.Runtime;

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
/// The results are JSEAL values — the wrapper the host hands back and the array the realm mints — and the
/// one thing that has not moved is the call frame. Both entry points are registered from
/// <c>DomBridge/Registration/Document.cs</c>, which has not migrated, so the coordinate read is still an
/// engine one and the answer is unwrapped back to an engine value here. <see cref="Coordinate"/> is the
/// bridge's former <c>DomBridge.GetCoordinateArgument</c>, moved into its only caller so that
/// <c>DomBridge/HitTesting.cs</c> — 582 lines of pure geometry — no longer names an engine type for the
/// sake of one argument read; it becomes <c>call.Realm.ToNumber(call[i])</c> when that registration site
/// migrates.
/// </remarks>
internal static class HitTestBinding
{
    public static JSValue ElementFromPoint(IHitTestHost host, in Arguments a)
    {
        var hit = host.HitTestDocumentPoint(
            host.DocumentElement, Coordinate(a, 0), Coordinate(a, 1)).FirstOrDefault();
        return hit != null
            ? Runtime.JsInterop.ToEngineObject(host.WrapNode(hit))
            : JavaScript.BuiltIns.Null.JSNull.Value;
    }

    public static JSValue ElementsFromPoint(IHitTestHost host, in Arguments a)
    {
        var hits = host.HitTestDocumentPoint(
            host.DocumentElement, Coordinate(a, 0), Coordinate(a, 1));
        return Runtime.JsInterop.ToEngineObject(host.Realm.NewArray([.. hits.Select(host.WrapNode)]));
    }

    /// <summary>
    /// One of <c>elementFromPoint</c>'s two coordinates: the number the page passed, and NaN when it
    /// passed nothing, <c>null</c> or <c>undefined</c> — which the hit test reads as "no such point"
    /// and answers with an empty stack.
    /// </summary>
    private static double Coordinate(in Arguments args, int index) =>
        args.Length > index && !args[index].IsNull && !args[index].IsUndefined
            ? args[index].DoubleValue
            : double.NaN;
}
