using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

// Engine-typed only for the three entry points below, whose caller is an unmigrated registration site
// with an engine call frame: DomBridge/ElementInterfaces.cs installs <object>.data, .contentDocument
// and getSVGDocument() as DomFunctions and hands each an `in Arguments`.
using Broiler.JavaScript.Runtime;

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
/// <remarks>
/// The sub-document is a JSEAL <see cref="JsValue"/> handle throughout — that is what the host contract
/// hands back. What has <em>not</em> moved is the call frame: all three members are registered from
/// <c>DomBridge/ElementInterfaces.cs</c>, which has not migrated, so the argument read and the returned
/// value are still engine ones and the unwrapping happens here. The coercion is unchanged — the same
/// ECMAScript <c>ToString</c> on the same argument — and it becomes <c>call.Realm.ToJsString(call[0])</c>
/// when that registration site migrates.
/// </remarks>
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
            return JavaScript.BuiltIns.Null.JSNull.Value;
        // Check if the resource actually loaded successfully
        if (host.IsObjectLoadFailed(element))
            return JavaScript.BuiltIns.Null.JSNull.Value;
        return Runtime.JsInterop.ToEngineObject(host.GetOrCreateSubDocument(element));
    }

    // <object>.getSVGDocument() — same-origin sub-document (no load-failure gate).
    public static JSValue GetSvgDocument(IObjectElementHost host, DomElement element, in Arguments _)
    {
        var dataUrl = DomBridge.TryGetAttribute(element, "data", out var d) ? d : string.Empty;
        if (DomBridge.IsCrossOrigin(dataUrl, host.PageUrl))
            return JavaScript.BuiltIns.Null.JSNull.Value;
        return Runtime.JsInterop.ToEngineObject(host.GetOrCreateSubDocument(element));
    }
}
