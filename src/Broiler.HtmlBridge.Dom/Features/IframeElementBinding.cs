using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>&lt;iframe&gt;</c>-element browsing-context IDL accessors, co-located as an HtmlBridge feature
/// module: <c>contentDocument</c> / <c>contentWindow</c> / <c>getSVGDocument()</c> (each the
/// same-origin sub-document or sub-window, or <c>null</c> across origins), the <c>src</c> / <c>srcdoc</c>
/// read/write pair (whose setters reload the frame), and the read-only <c>sandbox</c> reflection. The
/// browsing-context machinery is reached through the <see cref="IIframeElementHost"/> contract; the plain
/// content-attribute reads/writes use the bridge's neutral <c>internal static</c> <c>SetAttr</c>/
/// <c>TryGetAttribute</c> helpers directly. Sibling of the <c>&lt;object&gt;</c> <c>ObjectElementBinding</c>.
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>) throughout, installer and accessor
/// bodies alike, so this file names no engine type. The element-wrapper hub that installs these
/// members (<c>DomBridge/JsObjects.cs</c>) mints the wrapper through the realm and passes the handle.
/// </remarks>
internal static class IframeElementBinding
{
    /// <summary>
    /// Installs the <c>&lt;iframe&gt;</c> browsing-context accessors on <paramref name="obj"/> when
    /// <paramref name="element"/> is an <c>&lt;iframe&gt;</c>. A no-op for other elements.
    /// </summary>
    public static void Install(IIframeElementHost host, JsValue obj, DomElement element)
    {
        // contentWindow / contentDocument — for <iframe> elements with full sub-document DOM
        if (!string.Equals(element.TagName, "iframe", StringComparison.OrdinalIgnoreCase))
            return;

        var realm = host.Realm;

        realm.DefineAccessor(obj, "contentDocument",
            (in _) => GetContentDocument(host, element), null);

        realm.DefineAccessor(obj, "contentWindow",
            (in _) => GetContentWindow(host, element), null);

        // getSVGDocument() — returns contentDocument (same as contentDocument for same-origin)
        realm.DefineMethod(obj, "getSVGDocument", 0, (in _) => GetContentDocument(host, element));

        // src property (read/write) — for iframe elements
        realm.DefineAccessor(obj, "src",
            (in _) => JsValue.String(DomBridgeUtils.TryGetAttribute(element, "src", out var s) ? s : string.Empty),
            (in call) => SetFrameAttribute(host, element, "src", in call));

        realm.DefineAccessor(obj, "srcdoc",
            (in _) => JsValue.String(DomBridgeUtils.TryGetAttribute(element, "srcdoc", out var s) ? s : string.Empty),
            (in call) => SetFrameAttribute(host, element, "srcdoc", in call));

        // sandbox attribute access
        realm.DefineAccessor(obj, "sandbox",
            (in _) => JsValue.String(DomBridgeUtils.TryGetAttribute(element, "sandbox", out var sandbox) ? sandbox : string.Empty),
            null);
    }

    // contentDocument / getSVGDocument — same-origin sub-document, or null across origins.
    private static JsValue GetContentDocument(IIframeElementHost host, DomElement element)
    {
        // Cross-origin iframes return null for contentDocument (same-origin policy)
        if (host.IsCurrentIframeCrossOrigin(element))
            return JsValue.Null;
        // Non-HTML resources get a minimal empty sub-document (no parsed fallback content)
        return host.GetOrCreateSubDocument(element);
    }

    private static JsValue GetContentWindow(IIframeElementHost host, DomElement element)
    {
        if (host.IsCurrentIframeCrossOrigin(element))
            return JsValue.Null;
        return host.GetOrCreateSubWindow(element);
    }

    // src / srcdoc setter — writes the content attribute and reloads the frame (invalidate cached
    // sub-document, clear the fired-onload latch, fire onload for the new resource).
    private static JsValue SetFrameAttribute(IIframeElementHost host, DomElement element, string attribute, in JsCall call)
    {
        // The realm's ToString, not the handle's: `frame.src = url` is the observable ECMAScript
        // coercion, and a page assigning a URL object or a template literal wrapper depends on it.
        DomBridgeUtils.SetAttr(element, attribute, call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty);
        // Invalidate cached sub-document when the frame source changes
        host.InvalidateCachedSubDocument(element);
        host.ClearOnloadFired(element);
        // Fire onload for the new resource
        host.FireSubDocumentOnload(element);
        return JsValue.Undefined;
    }
}
