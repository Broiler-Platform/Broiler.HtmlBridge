using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

// Engine-typed only for the three adapters at the foot of this file, whose caller is an unmigrated
// registration site with an engine call frame: DomBridge/ElementInterfaces.cs installs <object>.data,
// .contentDocument and getSVGDocument() as engine functions and hands each an `in Arguments`.
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
/// <para>
/// The sub-document is a JSEAL <see cref="JsValue"/> handle throughout — that is what the host contract
/// hands back — and each operation below is written in JSEAL: a <see cref="JsCall"/> frame for the one
/// member that reads an argument, and no frame at all for the two that do not.
/// </para>
/// <para>
/// <b>What has not moved is the call frame, and it is pinned from outside.</b> All three members are
/// registered from <c>DomBridge/ElementInterfaces.cs</c>, which still mints engine functions, so the
/// three adapters at the foot of this file take the engine's argument frame and hand back an engine
/// value. Each is one line over the operation above it, so the two spellings cannot drift: when that
/// registration site migrates it calls the <see cref="JsCall"/> overload and the adapters are deleted.
/// The one behavioural difference the deletion makes is where the <c>data</c> setter's <c>ToString</c>
/// comes from — the engine's own coercion today, the realm's afterwards — and those are the same
/// ECMAScript operation, so a page observes no change.
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
        DomBridge.SetAttr(element, "data", dataUrl);
        host.InvalidateCachedSubDocument(element);
    }

    /// <summary>
    /// <c>&lt;object&gt;.contentDocument</c> — the same-origin sub-document, or <c>null</c> when the
    /// resource is cross-origin or failed to load (so the fallback child content is visible).
    /// </summary>
    internal static JsValue ContentDocument(IObjectElementHost host, DomElement element)
    {
        var dataUrl = DomBridge.TryGetAttribute(element, "data", out var d) ? d : string.Empty;
        if (DomBridge.IsCrossOrigin(dataUrl, host.PageUrl))
            return JsValue.Null;
        // Check if the resource actually loaded successfully
        if (host.IsObjectLoadFailed(element))
            return JsValue.Null;
        return host.GetOrCreateSubDocument(element);
    }

    /// <summary><c>&lt;object&gt;.getSVGDocument()</c> — the same-origin sub-document, with no
    /// load-failure gate.</summary>
    internal static JsValue SvgDocument(IObjectElementHost host, DomElement element)
    {
        var dataUrl = DomBridge.TryGetAttribute(element, "data", out var d) ? d : string.Empty;
        if (DomBridge.IsCrossOrigin(dataUrl, host.PageUrl))
            return JsValue.Null;
        return host.GetOrCreateSubDocument(element);
    }

    // -------- The engine-typed adapters; see the remarks on this class --------

    /// <inheritdoc cref="SetData(IObjectElementHost, DomElement, in JsCall)" />
    public static JSValue SetData(IObjectElementHost host, DomElement element, in Arguments a)
    {
        SetData(host, element, a.Length > 0 ? a[0].ToString() : string.Empty);
        return JSUndefined.Value;
    }

    /// <inheritdoc cref="ContentDocument" />
    public static JSValue GetContentDocument(IObjectElementHost host, DomElement element, in Arguments _) =>
        ToEngineResult(ContentDocument(host, element));

    /// <inheritdoc cref="SvgDocument" />
    public static JSValue GetSvgDocument(IObjectElementHost host, DomElement element, in Arguments _) =>
        ToEngineResult(SvgDocument(host, element));

    /// <summary>
    /// A sub-document (or the <c>null</c> the two gates yield) as the engine value the unmigrated
    /// registration site takes back.
    /// </summary>
    /// <remarks>
    /// <see cref="Runtime.JsInterop"/> carries an object across without converting it, which is why the
    /// null arm names the engine's own singleton instead: a JSEAL primitive has no engine instance to
    /// hand back. Null is the only primitive these two produce, and anything else is asked to be an
    /// object — so a handle that is neither still fails at the seam, exactly as it did when the seam
    /// was written out at each return.
    /// </remarks>
    private static JSValue ToEngineResult(JsValue value) =>
        value.IsNull
            ? JavaScript.BuiltIns.Null.JSNull.Value
            : Runtime.JsInterop.ToEngineObject(value);
}
