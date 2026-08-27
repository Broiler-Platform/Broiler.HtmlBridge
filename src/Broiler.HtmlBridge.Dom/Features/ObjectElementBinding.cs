using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>&lt;object&gt;</c>-element sub-document IDL accessors, co-located as an HtmlBridge feature module
/// (Phase 3): the <c>data</c> content-attribute <b>setter</b> (which invalidates the cached sub-document so a
/// new <c>data</c> URL reloads), and the <c>contentDocument</c> getter / <c>getSVGDocument()</c> method,
/// which resolve to the lazily-built sub-document when the resource is same-origin (and, for
/// <c>contentDocument</c>, actually loaded — otherwise <c>null</c>, so the element's fallback content shows).
/// The plain reflected <c>data</c> getter and the <c>type</c> get/set live in <see cref="ElementReflectionBinding"/>
/// (P3.49); this module owns only the parts coupled to the sub-document / browsing-context machinery, reached
/// through the narrow <see cref="IObjectElementHost"/> contract. The content-attribute write and the
/// same-origin test use the bridge's neutral <c>internal static</c> <c>SetAttr</c>/<c>TryGetAttribute</c>/
/// <c>IsCrossOrigin</c> helpers directly. Was the bridge's
/// <c>JsElementInterfacesSetData051Core</c>/<c>GetContentDocument054Core</c>/<c>GetSVGDocument055Core</c>.
/// </summary>
internal static class ObjectElementBinding
{
    // <object>.data setter — writes the content attribute and invalidates the cached sub-document.
    public static JSValue SetData(IObjectElementHost host, DomElement element, in Arguments a)
    {
        DomBridge.SetAttr(element, "data", a.Length > 0 ? a[0].ToString() : string.Empty);
        host.InvalidateCachedSubDocument(element);
        return JSUndefined.Value;
    }

    // <object>.contentDocument getter — same-origin sub-document, or null if cross-origin or load-failed
    // (so the fallback child content is visible).
    public static JSValue GetContentDocument(IObjectElementHost host, DomElement element, in Arguments _)
    {
        var dataUrl = DomBridge.TryGetAttribute(element, "data", out var d) ? d : string.Empty;
        if (DomBridge.IsCrossOrigin(dataUrl, host.PageUrl))
            return JSNull.Value;
        // Check if the resource actually loaded successfully
        if (host.IsObjectLoadFailed(element))
            return JSNull.Value;
        return host.GetOrCreateSubDocument(element);
    }

    // <object>.getSVGDocument() — same-origin sub-document (no load-failure gate).
    public static JSValue GetSvgDocument(IObjectElementHost host, DomElement element, in Arguments _)
    {
        var dataUrl = DomBridge.TryGetAttribute(element, "data", out var d) ? d : string.Empty;
        if (DomBridge.IsCrossOrigin(dataUrl, host.PageUrl))
            return JSNull.Value;
        return host.GetOrCreateSubDocument(element);
    }
}
