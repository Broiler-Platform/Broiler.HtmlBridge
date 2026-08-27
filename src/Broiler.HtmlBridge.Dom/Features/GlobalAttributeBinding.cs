using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The global content-attribute reflectors, co-located as an HtmlBridge feature module
/// (Phase 3): <c>id</c>, <c>className</c> (↔ <c>class</c>), <c>title</c>, <c>lang</c>, <c>accessKey</c>
/// (↔ <c>accesskey</c>), <c>dir</c>, and the enumerated <c>draggable</c>. They are installed in two
/// halves because Web IDL splits them in two: <c>id</c> and <c>className</c> belong to
/// <c>Element</c> and live on its prototype, the other five to <c>HTMLElement</c>. The selector-affecting three
/// (<c>id</c>/<c>className</c>/<c>dir</c>) invalidate the style scope on write through the one-member
/// <see cref="IGlobalAttributeHost"/> contract; everything else is a plain reflected read/write over the
/// bridge's neutral <c>internal static</c> <c>SetAttr</c>/<c>TryGetAttribute</c> helpers, and the canonical
/// <c>id</c>/<c>class</c> mirrors are kept on <see cref="DomElement.Id"/>/<see cref="DomElement.ClassName"/>
/// directly. Was the bridge's <c>JsJsObjectsSetId002Core</c>/<c>GetClassName003Core</c>/<c>SetClassName004Core</c>/
/// <c>SetTitle006Core</c>/<c>SetLang008Core</c>/<c>SetAccessKey010Core</c>/<c>SetDir012Core</c>/
/// <c>GetDraggable013Core</c>/<c>SetDraggable014Core</c>.
/// </summary>
internal static class GlobalAttributeBinding
{
    /// <summary>
    /// <c>id</c> and <c>className</c> — the two reflectors DOM §4.9 puts on <c>Element</c> rather than
    /// on <c>HTMLElement</c>, so they go on <c>Element.prototype</c> and an SVG element has them too.
    /// </summary>
    public static void InstallElementMembers(IGlobalAttributeHost host, JSObject target, ElementSource element)
    {
        target.FastAddProperty("id",
            new DomFunction((in a) => element(in a, "id") is { Id: { } id } ? new JSString(id) : JSNull.Value, "get id"),
            new DomFunction((in a) => SetId(host, element(in a, "id"), in a), "set id"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // className (read/write) — reflects the 'class' content attribute
        target.FastAddProperty("className",
            new DomFunction((in a) => GetClassName(element(in a, "className")), "get className"),
            new DomFunction((in a) => SetClassName(host, element(in a, "className"), in a), "set className"),
            JSPropertyAttributes.EnumerableConfigurableProperty);
    }

    /// <summary>
    /// The reflectors <c>HTMLElement</c> owns: <c>title</c>, <c>lang</c>, <c>accessKey</c>,
    /// <c>dir</c> and the enumerated <c>draggable</c>. Still installed on each wrapper, until
    /// <c>HTMLElement</c>'s own interface moves.
    /// </summary>
    public static void InstallHtmlElementMembers(IGlobalAttributeHost host, JSObject target, ElementSource element)
    {
        // title (read/write) — synced with attributes["title"]
        target.FastAddProperty("title",
            new DomFunction((in a) => ReflectedGet(element(in a, "title"), "title"), "get title"),
            new DomFunction((in a) => ReflectedSet(element(in a, "title"), "title", in a), "set title"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // lang (read/write) — synced with attributes["lang"]
        target.FastAddProperty("lang",
            new DomFunction((in a) => ReflectedGet(element(in a, "lang"), "lang"), "get lang"),
            new DomFunction((in a) => ReflectedSet(element(in a, "lang"), "lang", in a), "set lang"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // accessKey (read/write) — synced with attributes["accesskey"]
        target.FastAddProperty("accessKey",
            new DomFunction((in a) => ReflectedGet(element(in a, "accessKey"), "accesskey"), "get accessKey"),
            new DomFunction((in a) => ReflectedSet(element(in a, "accessKey"), "accesskey", in a), "set accessKey"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // dir (read/write) — synced with attributes["dir"]
        target.FastAddProperty("dir",
            new DomFunction((in a) => ReflectedGet(element(in a, "dir"), "dir"), "get dir"),
            new DomFunction((in a) => SetDir(host, element(in a, "dir"), in a), "set dir"),
            JSPropertyAttributes.EnumerableConfigurableProperty);

        // draggable (read/write) — reflected enumerated attribute
        target.FastAddProperty("draggable",
            new DomFunction((in a) => GetDraggable(element(in a, "draggable")), "get draggable"),
            new DomFunction((in a) => SetDraggable(element(in a, "draggable"), in a), "set draggable"),
            JSPropertyAttributes.EnumerableConfigurableProperty);
    }

    private static JSValue SetId(IGlobalAttributeHost host, DomElement element, in Arguments a)
    {
        var val = a.Length > 0 ? a[0].ToString() : string.Empty;
        element.Id = val;
        DomBridge.SetAttr(element, "id", val);
        host.InvalidateStyleScope(element);
        return JSUndefined.Value;
    }

    private static JSValue GetClassName(DomElement element)
    {
        // Prefer Attributes['class'] (synced by setAttribute and className setter).
        // Fall back to element.ClassName for elements created with a class in the constructor
        // but not yet synced to Attributes (e.g. parsed HTML elements).
        if (DomBridge.TryGetAttribute(element, "class", out var cls))
            return new JSString(cls);
        return element.ClassName != null ? new JSString(element.ClassName) : new JSString(string.Empty);
    }

    private static JSValue SetClassName(IGlobalAttributeHost host, DomElement element, in Arguments a)
    {
        var val = a.Length > 0 ? a[0].ToString() : string.Empty;
        element.ClassName = val;
        DomBridge.SetAttr(element, "class", val);
        host.InvalidateStyleScope(element);
        return JSUndefined.Value;
    }

    private static JSValue SetDir(IGlobalAttributeHost host, DomElement element, in Arguments a)
    {
        DomBridge.SetAttr(element, "dir", a.Length > 0 ? a[0].ToString() : string.Empty);
        host.InvalidateStyleScope(element);
        return JSUndefined.Value;
    }

    private static JSValue GetDraggable(DomElement element)
    {
        if (DomBridge.TryGetAttribute(element, "draggable", out var draggable))
            return string.Equals(draggable, "true", StringComparison.OrdinalIgnoreCase) ? JSBoolean.True : JSBoolean.False;
        return JSBoolean.False;
    }

    private static JSValue SetDraggable(DomElement element, in Arguments a)
    {
        DomBridge.SetAttr(element, "draggable", a.Length > 0 && a[0].BooleanValue ? "true" : "false");
        return JSUndefined.Value;
    }

    // Plain reflected string getter (default empty), shared by title/lang/accessKey.
    private static JSValue ReflectedGet(DomElement element, string attribute)
        => DomBridge.TryGetAttribute(element, attribute, out var v) ? new JSString(v) : new JSString(string.Empty);

    // Plain reflected string setter (default empty), shared by title/lang/accessKey.
    private static JSValue ReflectedSet(DomElement element, string attribute, in Arguments a)
    {
        DomBridge.SetAttr(element, attribute, a.Length > 0 ? a[0].ToString() : string.Empty);
        return JSUndefined.Value;
    }
}
