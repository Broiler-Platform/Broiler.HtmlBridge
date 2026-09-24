using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>&lt;object&gt;</c>-element sub-document IDL accessors, co-located as an HtmlBridge feature module:
/// the <c>data</c> content-attribute <b>setter</b> (which invalidates the cached sub-document so a
/// new <c>data</c> URL reloads), and the <c>contentDocument</c> getter / <c>getSVGDocument()</c> method,
/// which resolve to the lazily-built sub-document when the resource is same-origin (and, for
/// <c>contentDocument</c>, actually loaded — otherwise <c>null</c>, so the element's fallback content shows).
/// The plain reflected <c>data</c> getter and the <c>type</c> get/set live in <see cref="ElementReflectionBinding"/>;
/// this module owns only the parts coupled to the sub-document / browsing-context machinery, reached
/// through the narrow <see cref="IObjectElementHost"/> contract. The content-attribute write and the
/// same-origin test use the bridge's neutral <c>internal static</c> <c>SetAttr</c>/<c>TryGetAttribute</c>/
/// <c>IsCrossOrigin</c> helpers directly.
/// </summary>
/// <remarks>
/// <para>
/// The sub-document is a JSEAL <see cref="JsValue"/> handle throughout — that is what the host contract
/// hands back — and each operation below is written in JSEAL: a <see cref="JsCall"/> frame for the one
/// member that reads an argument, and no frame at all for the two that do not.
/// </para>
/// <para>
/// <b>The call frame is JSEAL's too.</b> All three members are registered from
/// <c>DomBridge/NodeInterfaces.cs</c>, which mints them through the realm and calls the operations
/// above directly. The <c>data</c> setter's <c>ToString</c> is the realm's, which is the same
/// ECMAScript operation an engine's own coercion performs.
/// </para>
/// </remarks>
internal static class ObjectElementBinding
{
    // -------- The operations --------

    /// <summary><c>&lt;object&gt;.data</c>'s setter — writes the content attribute and invalidates the
    /// cached sub-document, so a new <c>data</c> URL reloads.</summary>
    internal static JsValue SetData(IObjectElementHost host, DomElement element, in JsCall call)
    {
        // The realm's ToString, not the handle's: assigning an object to `obj.data` runs that object's
        // own toString, which is the coercion a page observes in the attribute afterwards.
        SetData(host, element, call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty);
        return JsValue.Undefined;
    }

    /// <summary>The write itself, taking the already-coerced URL so that the coercion stays where the
    /// argument is — see the remarks on this class.</summary>
    internal static void SetData(IObjectElementHost host, DomElement element, string dataUrl)
    {
        DomBridgeUtils.SetAttr(element, "data", dataUrl);
        host.InvalidateCachedSubDocument(element);
    }

    /// <summary>
    /// <c>&lt;object&gt;.contentDocument</c> — the same-origin sub-document, or <c>null</c> when the
    /// resource is cross-origin or failed to load (so the fallback child content is visible).
    /// </summary>
    internal static JsValue ContentDocument(IObjectElementHost host, DomElement element)
    {
        var dataUrl = DomBridgeUtils.TryGetAttribute(element, "data", out var d) ? d : string.Empty;
        if (DomBridgeUtils.IsCrossOrigin(dataUrl, host.PageUrl))
            return JsValue.Null;
        // Check if the resource actually loaded successfully
        if (host.IsObjectLoadFailed(element))
            return JsValue.Null;
        var document = host.GetOrCreateSubDocument(element);
        return host.IsFrameDocumentCrossOrigin(element) ? JsValue.Null : document;
    }

    /// <summary><c>&lt;object&gt;.getSVGDocument()</c> — the same-origin sub-document, with no
    /// load-failure gate.</summary>
    internal static JsValue SvgDocument(IObjectElementHost host, DomElement element)
    {
        var dataUrl = DomBridgeUtils.TryGetAttribute(element, "data", out var d) ? d : string.Empty;
        if (DomBridgeUtils.IsCrossOrigin(dataUrl, host.PageUrl))
            return JsValue.Null;
        var document = host.GetOrCreateSubDocument(element);
        return host.IsFrameDocumentCrossOrigin(element) ? JsValue.Null : document;
    }
}
