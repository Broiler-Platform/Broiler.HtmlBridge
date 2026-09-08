using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>document</c> structural accessors — <c>document.body</c>, <c>document.head</c> (getters
/// returning the first matching child of the documentElement) and <c>document.title</c> (get/set) —
/// co-located as an HtmlBridge feature module (Phase 3). The document root, wrapper factory and title
/// are reached through the narrow <see cref="IDocumentStructureHost"/> contract; child enumeration
/// uses the bridge's neutral <c>internal static</c> <c>ChildElements</c> directly. Previously the
/// bridge's <c>JsRegistrationGetBody002Core</c>/<c>GetHead003Core</c>/<c>SetTitle005Core</c> (and the
/// inline title getter) in the shared JsFunctionCallbacks/Registration.cs grab-bag.
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine
/// type: the realm arrives on the call frame, and the wrapper the host hands back is a
/// <see cref="JsValue"/> over the same engine object it always was.
/// </remarks>
internal static class DocumentStructureBinding
{
    public static JsValue GetBody(IDocumentStructureHost host, in JsCall call) => FindChild(host, "body");

    public static JsValue GetHead(IDocumentStructureHost host, in JsCall call) => FindChild(host, "head");

    public static JsValue GetTitle(IDocumentStructureHost host, in JsCall call) => JsValue.String(host.Title);

    public static JsValue SetTitle(IDocumentStructureHost host, in JsCall call)
    {
        // ToJsString, not the handle's rendering: assigning an object to document.title runs the
        // object's own toString, which is the coercion a page observes here.
        host.Title = call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
        return JsValue.Undefined;
    }

    private static JsValue FindChild(IDocumentStructureHost host, string tagName)
    {
        foreach (var child in DomBridge.ChildElements(host.DocumentElement))
        {
            if (string.Equals(child.TagName, tagName, StringComparison.OrdinalIgnoreCase))
                return host.ToJsObject(child);
        }

        return JsValue.Null;
    }
}
