using System;
using Broiler.Dom;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Reflected content-attribute IDL accessors for various HTML element interfaces, co-located as an
/// HtmlBridge feature module: the plain string reflectors (<c>label.htmlFor</c> ↔ <c>for</c>,
/// <c>meta.httpEquiv</c> ↔ <c>http-equiv</c>, <c>.type</c>, and the generic named string / numeric
/// dimension setters) and the URL-typed getters (<c>&lt;object&gt;.data</c>,
/// <c>&lt;a&gt;/&lt;area&gt;/&lt;base&gt;/&lt;link&gt;.href</c> and
/// <c>&lt;script&gt;/&lt;img&gt;.src</c>, with <c>&lt;img&gt;.currentSrc</c> alongside it), which
/// resolve their relative content
/// attribute against the live page URL. Content-attribute reads/writes use the bridge's neutral
/// <c>internal static</c> <c>TryGetAttribute</c>/<c>SetAttr</c> helpers directly; only the page URL — read
/// at call time — comes through the one-member <see cref="IElementReflectionHost"/> contract. The
/// byte-identical href get/set pairs are deduplicated here.
/// </summary>
/// <remarks>
/// <para>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine type.
/// Two shapes are worth naming because they are the ones a mechanical rewrite gets wrong:
/// </para>
/// <list type="bullet">
/// <item><description>
/// every setter coerces its argument with <see cref="IJsValues.ToJsString"/> rather than with the
/// handle's own <c>ToString</c>. These are content-attribute writes taking whatever a page assigned —
/// <c>img.alt = {toString(){…}}</c> is the ECMAScript coercion and it may run page script, which is
/// what the engine did before and what the handle deliberately does not do.
/// </description></item>
/// <item><description>
/// the four URL getters no longer take the call frame at all. They never read an argument from it, and
/// an unread parameter that exists only to satisfy a delegate shape is one more thing tying the module
/// to how its members happen to be installed.
/// </description></item>
/// </list>
/// </remarks>
internal static class ElementReflectionBinding
{
    public static JsValue SetHtmlFor(DomElement element, in JsCall call)
    {
        DomBridgeUtils.SetAttr(element, "for", StringArgument(in call));
        return JsValue.Undefined;
    }

    public static JsValue SetHttpEquiv(DomElement element, in JsCall call)
    {
        DomBridgeUtils.SetAttr(element, "http-equiv", StringArgument(in call));
        return JsValue.Undefined;
    }

    public static JsValue SetType(DomElement element, in JsCall call)
    {
        DomBridgeUtils.SetAttr(element, "type", StringArgument(in call));
        return JsValue.Undefined;
    }

    // <object>.data getter — reflected URL, resolved against the page URL.
    public static JsValue GetData(IElementReflectionHost host, DomElement element)
        => JsValue.String(ResolveReflectedUrl(host.PageUrl, element, "data"));

    // <a>/<area>/<base>/<link>.href getter — reflected URL, resolved against the page URL.
    public static JsValue GetHref(IElementReflectionHost host, DomElement element)
        => JsValue.String(ResolveReflectedUrl(host.PageUrl, element, "href"));

    // <script>/<img>.src getter — reflected URL, resolved against the page URL like href/data.
    // Absolute is what the IDL returns even when the content attribute is relative, which is what a
    // page comparing script.src against a known URL — or handing img.src to a URL parser — expects.
    public static JsValue GetSrc(IElementReflectionHost host, DomElement element)
        => JsValue.String(ResolveReflectedUrl(host.PageUrl, element, "src"));

    // <img>.currentSrc getter — read-only, the URL the element settled on. Srcset candidate selection
    // happens in layout and is not visible from here, so a src is reported as the URL that was chosen
    // and no src at all as the empty string: the two answers `img.currentSrc || img.src` is written
    // against. Never the bare page URL, which is what resolving an absent (or empty) src would give.
    public static JsValue GetCurrentSrc(IElementReflectionHost host, DomElement element)
        => DomBridgeUtils.TryGetAttribute(element, "src", out var value) && value.Length > 0
            ? GetSrc(host, element)
            : JsValue.String(string.Empty);

    public static JsValue SetSrc(DomElement element, in JsCall call)
    {
        DomBridgeUtils.SetAttr(element, "src", StringArgument(in call));
        return JsValue.Undefined;
    }

    public static JsValue SetHref(DomElement element, in JsCall call)
    {
        DomBridgeUtils.SetAttr(element, "href", StringArgument(in call));
        return JsValue.Undefined;
    }

    // Generic reflected-string setter (default empty), used for various named IDL attributes.
    public static JsValue SetReflectedAttribute(string? name, DomElement element, in JsCall call)
    {
        DomBridgeUtils.SetAttr(element, name, StringArgument(in call));
        return JsValue.Undefined;
    }

    // Generic reflected-dimension setter (default "0"), used for numeric presentation attributes.
    public static JsValue SetReflectedDimension(string? name, DomElement element, in JsCall call)
    {
        DomBridgeUtils.SetAttr(element, name, StringArgument(in call, missing: "0"));
        return JsValue.Undefined;
    }

    // Generic reflected-boolean setter: a boolean content attribute is present or absent, never
    // "false" — writing the string would make the IDL getter read back true.
    public static JsValue SetReflectedBoolean(string? name, DomElement element, in JsCall call)
    {
        if (call.Length > 0 && call[0].AsBoolean)
            DomBridgeUtils.SetAttr(element, name, string.Empty);
        else
            DomBridgeUtils.RemoveAttr(element, name!);
        return JsValue.Undefined;
    }

    /// <summary>
    /// Argument zero as a string — the ECMAScript coercion, which may run a <c>toString</c> the page
    /// wrote — or <paramref name="missing"/> when the setter was called with no argument at all.
    /// </summary>
    /// <remarks>
    /// The arity test is on the count rather than on the handle: a setter reached
    /// through <c>Reflect.set</c> with no value is the one call that has no argument zero, and it
    /// writes the default rather than the string <c>"undefined"</c>.
    /// </remarks>
    private static string StringArgument(in JsCall call, string missing = "")
        => call.Length > 0 ? call.Realm.ToJsString(call[0]) : missing;

    private static string ResolveReflectedUrl(string pageUrl, DomElement element, string attribute)
    {
        if (!DomBridgeUtils.TryGetAttribute(element, attribute, out var value))
            return string.Empty;
        // Resolve the relative content attribute against the page URL, mirroring the browser IDL getter.
        if (Uri.TryCreate(pageUrl, UriKind.Absolute, out var baseUri) && Uri.TryCreate(baseUri, value, out var resolved))
            return resolved.AbsoluteUri;
        return value;
    }
}
