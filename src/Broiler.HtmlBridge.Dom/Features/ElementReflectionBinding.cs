using System;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Reflected content-attribute IDL accessors for various HTML element interfaces, co-located as an
/// HtmlBridge feature module (Phase 3): the plain string reflectors (<c>label.htmlFor</c> ↔ <c>for</c>,
/// <c>meta.httpEquiv</c> ↔ <c>http-equiv</c>, <c>.type</c>, and the generic named string / numeric
/// dimension setters) and the URL-typed getters (<c>&lt;object&gt;.data</c>,
/// <c>&lt;a&gt;/&lt;area&gt;/&lt;base&gt;/&lt;link&gt;.href</c> and
/// <c>&lt;script&gt;/&lt;img&gt;.src</c>, with <c>&lt;img&gt;.currentSrc</c> alongside it), which
/// resolve their relative content
/// attribute against the live page URL. Content-attribute reads/writes use the bridge's neutral
/// <c>internal static</c> <c>TryGetAttribute</c>/<c>SetAttr</c> helpers directly; only the page URL — read
/// at call time — comes through the one-member <see cref="IElementReflectionHost"/> contract. Was the
/// bridge's <c>JsElementInterfacesSetHtmlFor047Core</c>/<c>SetHttpEquiv049Core</c>/<c>GetData050Core</c>/
/// <c>SetType053Core</c>/<c>GetHref056/060Core</c>/<c>SetHref057/061Core</c>/<c>Callback059/063Core</c>
/// (the byte-identical href get/set pairs are deduplicated here).
/// </summary>
internal static class ElementReflectionBinding
{
    public static JSValue SetHtmlFor(DomElement element, in Arguments a)
    {
        DomBridge.SetAttr(element, "for", a.Length > 0 ? a[0].ToString() : string.Empty);
        return JSUndefined.Value;
    }

    public static JSValue SetHttpEquiv(DomElement element, in Arguments a)
    {
        DomBridge.SetAttr(element, "http-equiv", a.Length > 0 ? a[0].ToString() : string.Empty);
        return JSUndefined.Value;
    }

    public static JSValue SetType(DomElement element, in Arguments a)
    {
        DomBridge.SetAttr(element, "type", a.Length > 0 ? a[0].ToString() : string.Empty);
        return JSUndefined.Value;
    }

    // <object>.data getter — reflected URL, resolved against the page URL.
    public static JSValue GetData(IElementReflectionHost host, DomElement element, in Arguments _)
        => new JSString(ResolveReflectedUrl(host.PageUrl, element, "data"));

    // <a>/<area>/<base>/<link>.href getter — reflected URL, resolved against the page URL.
    public static JSValue GetHref(IElementReflectionHost host, DomElement element, in Arguments _)
        => new JSString(ResolveReflectedUrl(host.PageUrl, element, "href"));

    // <script>/<img>.src getter — reflected URL, resolved against the page URL like href/data.
    // Absolute is what the IDL returns even when the content attribute is relative, which is what a
    // page comparing script.src against a known URL — or handing img.src to a URL parser — expects.
    public static JSValue GetSrc(IElementReflectionHost host, DomElement element, in Arguments _)
        => new JSString(ResolveReflectedUrl(host.PageUrl, element, "src"));

    // <img>.currentSrc getter — read-only, the URL the element settled on. Srcset candidate selection
    // happens in layout and is not visible from here, so a src is reported as the URL that was chosen
    // and no src at all as the empty string: the two answers `img.currentSrc || img.src` is written
    // against. Never the bare page URL, which is what resolving an absent (or empty) src would give.
    public static JSValue GetCurrentSrc(IElementReflectionHost host, DomElement element, in Arguments a)
        => DomBridge.TryGetAttribute(element, "src", out var value) && value.Length > 0
            ? GetSrc(host, element, in a)
            : new JSString(string.Empty);

    public static JSValue SetSrc(DomElement element, in Arguments a)
    {
        DomBridge.SetAttr(element, "src", a.Length > 0 ? a[0].ToString() : string.Empty);
        return JSUndefined.Value;
    }

    public static JSValue SetHref(DomElement element, in Arguments a)
    {
        DomBridge.SetAttr(element, "href", a.Length > 0 ? a[0].ToString() : string.Empty);
        return JSUndefined.Value;
    }

    // Generic reflected-string setter (default empty), used for various named IDL attributes.
    public static JSValue SetReflectedAttribute(string? name, DomElement element, in Arguments a)
    {
        DomBridge.SetAttr(element, name, a.Length > 0 ? a[0].ToString() : string.Empty);
        return JSUndefined.Value;
    }

    // Generic reflected-dimension setter (default "0"), used for numeric presentation attributes.
    public static JSValue SetReflectedDimension(string? name, DomElement element, in Arguments a)
    {
        DomBridge.SetAttr(element, name, a.Length > 0 ? a[0].ToString() : "0");
        return JSUndefined.Value;
    }

    // Generic reflected-boolean setter: a boolean content attribute is present or absent, never
    // "false" — writing the string would make the IDL getter read back true.
    public static JSValue SetReflectedBoolean(string? name, DomElement element, in Arguments a)
    {
        if (a.Length > 0 && a[0].BooleanValue)
            DomBridge.SetAttr(element, name, string.Empty);
        else
            DomBridge.RemoveAttr(element, name!);
        return JSUndefined.Value;
    }

    private static string ResolveReflectedUrl(string pageUrl, DomElement element, string attribute)
    {
        if (!DomBridge.TryGetAttribute(element, attribute, out var value))
            return string.Empty;
        // Resolve the relative content attribute against the page URL, mirroring the browser IDL getter.
        if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri) && Uri.TryCreate(baseUri, value, out var resolved))
            return resolved.AbsoluteUri;
        return value;
    }
}
