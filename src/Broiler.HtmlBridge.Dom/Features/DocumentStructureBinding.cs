using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.String;

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
internal static class DocumentStructureBinding
{
    public static JSValue GetBody(IDocumentStructureHost host, in Arguments a) => FindChild(host, "body");

    public static JSValue GetHead(IDocumentStructureHost host, in Arguments a) => FindChild(host, "head");

    public static JSValue GetTitle(IDocumentStructureHost host, in Arguments a) => new JSString(host.Title);

    public static JSValue SetTitle(IDocumentStructureHost host, in Arguments a)
    {
        host.Title = a.Length > 0 ? a[0].ToString() : string.Empty;
        return JSUndefined.Value;
    }

    private static JSValue FindChild(IDocumentStructureHost host, string tagName)
    {
        foreach (var child in DomBridge.ChildElements(host.DocumentElement))
        {
            if (string.Equals(child.TagName, tagName, StringComparison.OrdinalIgnoreCase))
                return host.ToJSObject(child);
        }

        return JSNull.Value;
    }
}
