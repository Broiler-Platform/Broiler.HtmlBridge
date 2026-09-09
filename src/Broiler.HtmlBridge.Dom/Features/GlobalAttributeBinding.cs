using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

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
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine type.
/// Every setter coerces with <see cref="IJsValues.ToJsString"/> and not with the handle's own
/// <c>ToString</c>: these are the reflectors a page assigns arbitrary values to — <c>el.className =
/// classListLikeObject</c> is the ECMAScript coercion, and running the object's <c>toString</c> is what
/// the engine was doing and what the handle deliberately does not do.
/// </remarks>
internal static class GlobalAttributeBinding
{
    /// <summary>
    /// <c>id</c> and <c>className</c> — the two reflectors DOM §4.9 puts on <c>Element</c> rather than
    /// on <c>HTMLElement</c>, so they go on <c>Element.prototype</c> and an SVG element has them too.
    /// </summary>
    public static void InstallElementMembers(IGlobalAttributeHost host, IJsRealm realm, JsValue target, JsElementSource element)
    {
        realm.DefineAccessor(target, "id",
            (in call) => element(in call, "id") is { Id: { } id } ? JsValue.String(id) : JsValue.Null,
            (in call) => SetId(host, element(in call, "id"), in call));

        // className (read/write) — reflects the 'class' content attribute
        realm.DefineAccessor(target, "className",
            (in call) => GetClassName(element(in call, "className")),
            (in call) => SetClassName(host, element(in call, "className"), in call));
    }

    /// <summary>
    /// The reflectors <c>HTMLElement</c> owns: <c>title</c>, <c>lang</c>, <c>accessKey</c>,
    /// <c>dir</c> and the enumerated <c>draggable</c>. Still installed on each wrapper, until
    /// <c>HTMLElement</c>'s own interface moves.
    /// </summary>
    public static void InstallHtmlElementMembers(IGlobalAttributeHost host, IJsRealm realm, JsValue target, JsElementSource element)
    {
        // title (read/write) — synced with attributes["title"]
        realm.DefineAccessor(target, "title",
            (in call) => ReflectedGet(element(in call, "title"), "title"),
            (in call) => ReflectedSet(element(in call, "title"), "title", in call));

        // lang (read/write) — synced with attributes["lang"]
        realm.DefineAccessor(target, "lang",
            (in call) => ReflectedGet(element(in call, "lang"), "lang"),
            (in call) => ReflectedSet(element(in call, "lang"), "lang", in call));

        // accessKey (read/write) — synced with attributes["accesskey"]
        realm.DefineAccessor(target, "accessKey",
            (in call) => ReflectedGet(element(in call, "accessKey"), "accesskey"),
            (in call) => ReflectedSet(element(in call, "accessKey"), "accesskey", in call));

        // dir (read/write) — synced with attributes["dir"]
        realm.DefineAccessor(target, "dir",
            (in call) => ReflectedGet(element(in call, "dir"), "dir"),
            (in call) => SetDir(host, element(in call, "dir"), in call));

        // draggable (read/write) — reflected enumerated attribute
        realm.DefineAccessor(target, "draggable",
            (in call) => GetDraggable(element(in call, "draggable")),
            (in call) => SetDraggable(element(in call, "draggable"), in call));
    }

    private static JsValue SetId(IGlobalAttributeHost host, DomElement element, in JsCall call)
    {
        var val = StringArgument(in call);
        element.Id = val;
        DomBridge.SetAttr(element, "id", val);
        host.InvalidateStyleScope(element);
        return JsValue.Undefined;
    }

    private static JsValue GetClassName(DomElement element)
    {
        // Prefer Attributes['class'] (synced by setAttribute and className setter).
        // Fall back to element.ClassName for elements created with a class in the constructor
        // but not yet synced to Attributes (e.g. parsed HTML elements).
        if (DomBridge.TryGetAttribute(element, "class", out var cls))
            return JsValue.String(cls);
        return element.ClassName != null ? JsValue.String(element.ClassName) : JsValue.String(string.Empty);
    }

    private static JsValue SetClassName(IGlobalAttributeHost host, DomElement element, in JsCall call)
    {
        var val = StringArgument(in call);
        element.ClassName = val;
        DomBridge.SetAttr(element, "class", val);
        host.InvalidateStyleScope(element);
        return JsValue.Undefined;
    }

    private static JsValue SetDir(IGlobalAttributeHost host, DomElement element, in JsCall call)
    {
        DomBridge.SetAttr(element, "dir", StringArgument(in call));
        host.InvalidateStyleScope(element);
        return JsValue.Undefined;
    }

    private static JsValue GetDraggable(DomElement element)
    {
        if (DomBridge.TryGetAttribute(element, "draggable", out var draggable))
            return JsValue.Boolean(string.Equals(draggable, "true", StringComparison.OrdinalIgnoreCase));
        return JsValue.False;
    }

    private static JsValue SetDraggable(DomElement element, in JsCall call)
    {
        DomBridge.SetAttr(element, "draggable", call.Length > 0 && call[0].AsBoolean ? "true" : "false");
        return JsValue.Undefined;
    }

    // Plain reflected string getter (default empty), shared by title/lang/accessKey.
    private static JsValue ReflectedGet(DomElement element, string attribute)
        => DomBridge.TryGetAttribute(element, attribute, out var v) ? JsValue.String(v) : JsValue.String(string.Empty);

    // Plain reflected string setter (default empty), shared by title/lang/accessKey.
    private static JsValue ReflectedSet(DomElement element, string attribute, in JsCall call)
    {
        DomBridge.SetAttr(element, attribute, StringArgument(in call));
        return JsValue.Undefined;
    }

    /// <summary>
    /// Argument zero as a string — the ECMAScript coercion, which may run a <c>toString</c> the page
    /// wrote — or the empty string when the setter was called with no argument at all.
    /// </summary>
    private static string StringArgument(in JsCall call)
        => call.Length > 0 ? call.Realm.ToJsString(call[0]) : string.Empty;
}
