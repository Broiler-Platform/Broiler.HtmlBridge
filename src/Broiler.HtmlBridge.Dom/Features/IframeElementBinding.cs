using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>&lt;iframe&gt;</c>-element browsing-context IDL accessors, co-located as an HtmlBridge feature
/// module (Phase 3): <c>contentDocument</c> / <c>contentWindow</c> / <c>getSVGDocument()</c> (each the
/// same-origin sub-document or sub-window, or <c>null</c> across origins), the <c>src</c> / <c>srcdoc</c>
/// read/write pair (whose setters reload the frame), and the read-only <c>sandbox</c> reflection. The
/// browsing-context machinery is reached through the <see cref="IIframeElementHost"/> contract; the plain
/// content-attribute reads/writes use the bridge's neutral <c>internal static</c> <c>SetAttr</c>/
/// <c>TryGetAttribute</c> helpers directly. Sibling of the P3.52 <c>&lt;object&gt;</c> <c>ObjectElementBinding</c>.
/// Was the bridge's <c>JsJsObjectsGetContentDocument135Core</c>/<c>GetContentWindow136Core</c>/
/// <c>GetSVGDocument137Core</c>/<c>SetSrc139Core</c>/<c>SetSrcdoc141Core</c>.
/// </summary>
internal static class IframeElementBinding
{
    /// <summary>
    /// Installs the <c>&lt;iframe&gt;</c> browsing-context accessors on <paramref name="obj"/> when
    /// <paramref name="element"/> is an <c>&lt;iframe&gt;</c>. A no-op for other elements.
    /// </summary>
    public static void Install(IIframeElementHost host, JSObject obj, DomElement element)
    {
        // contentWindow / contentDocument — for <iframe> elements with full sub-document DOM
        if (!string.Equals(element.TagName, "iframe", StringComparison.OrdinalIgnoreCase))
            return;

        obj.FastAddProperty("contentDocument",
            new DomFunction((in _) => GetContentDocument(host, element), "get contentDocument"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        obj.FastAddProperty("contentWindow",
            new DomFunction((in _) => GetContentWindow(host, element), "get contentWindow"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        // getSVGDocument() — returns contentDocument (same as contentDocument for same-origin)
        obj.FastAddValue("getSVGDocument",
            new DomFunction((in _) => GetContentDocument(host, element), "getSVGDocument", 0),
            JSPropertyAttributes.EnumerableConfigurableValue);

        // src property (read/write) — for iframe elements
        obj.FastAddProperty("src",
            new DomFunction((in _) => DomBridge.TryGetAttribute(element, "src", out var s) ? new JSString(s) : new JSString(string.Empty), "get src"),
            new DomFunction((in a) => SetFrameAttribute(host, element, "src", in a), "set src"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        obj.FastAddProperty("srcdoc",
            new DomFunction((in _) => DomBridge.TryGetAttribute(element, "srcdoc", out var s) ? new JSString(s) : new JSString(string.Empty), "get srcdoc"),
            new DomFunction((in a) => SetFrameAttribute(host, element, "srcdoc", in a), "set srcdoc"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // sandbox attribute access
        obj.FastAddProperty("sandbox",
            new DomFunction((in _) => DomBridge.TryGetAttribute(element, "sandbox", out var sandbox) ? new JSString(sandbox) : new JSString(string.Empty), "get sandbox"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);
    }

    // contentDocument / getSVGDocument — same-origin sub-document, or null across origins.
    private static JSValue GetContentDocument(IIframeElementHost host, DomElement element)
    {
        // Cross-origin iframes return null for contentDocument (same-origin policy)
        if (host.IsCurrentIframeCrossOrigin(element))
            return JSNull.Value;
        // Non-HTML resources get a minimal empty sub-document (no parsed fallback content)
        return host.GetOrCreateSubDocument(element);
    }

    private static JSValue GetContentWindow(IIframeElementHost host, DomElement element)
    {
        if (host.IsCurrentIframeCrossOrigin(element))
            return JSNull.Value;
        return host.GetOrCreateSubWindow(element);
    }

    // src / srcdoc setter — writes the content attribute and reloads the frame (invalidate cached
    // sub-document, clear the fired-onload latch, fire onload for the new resource).
    private static JSValue SetFrameAttribute(IIframeElementHost host, DomElement element, string attribute, in Arguments a)
    {
        DomBridge.SetAttr(element, attribute, a.Length > 0 ? a[0].ToString() : string.Empty);
        // Invalidate cached sub-document when the frame source changes
        host.InvalidateCachedSubDocument(element);
        host.ClearOnloadFired(element);
        // Fire onload for the new resource
        host.FireSubDocumentOnload(element);
        return JSUndefined.Value;
    }
}
