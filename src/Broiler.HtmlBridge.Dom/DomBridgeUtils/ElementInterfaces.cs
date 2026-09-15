using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
    /// <summary>
    /// HTMLLinkElement's plain reflected DOMString IDL attributes (HTML §4.2.4), as
    /// IDL name → content-attribute name. Deliberately partial: <c>type</c> and <c>name</c> are
    /// already present because the form-control reflectors install on every element; <c>href</c> is
    /// URL-typed and wired separately; and <c>crossOrigin</c> (nullable + enumerated) and
    /// <c>disabled</c> (which toggles the sheet rather than the attribute — see
    /// <c>DomBridge/StyleSheets.cs</c>) are left out rather than approximated as plain strings.
    /// </summary>
    internal static readonly (string IdlName, string AttributeName)[] LinkReflectedAttributes =
    [
        ("rel", "rel"),
        ("as", "as"),
        ("media", "media"),
        ("hreflang", "hreflang"),
        ("integrity", "integrity"),
        ("referrerPolicy", "referrerpolicy"),
    ];

    /// <summary>
    /// HTMLScriptElement's plain reflected DOMString IDL attributes (HTML §4.12.1), as IDL name →
    /// content-attribute name. <c>src</c> is URL-typed and wired separately, and the boolean ones are
    /// in <see cref="ScriptReflectedBooleans"/>.
    /// </summary>
    internal static readonly (string IdlName, string AttributeName)[] ScriptReflectedAttributes =
    [
        ("type", "type"),
        ("charset", "charset"),
        ("integrity", "integrity"),
        ("crossOrigin", "crossorigin"),
        ("referrerPolicy", "referrerpolicy"),
        ("fetchPriority", "fetchpriority"),
        // Reflected plainly rather than with the spec's nonce-hiding (the content attribute is
        // cleared once the element is inserted, the IDL value kept): what matters here is that a
        // page setting s.nonce reaches the CSP check that authorises the script it is injecting.
        ("nonce", "nonce"),
    ];

    /// <summary>
    /// HTMLScriptElement's boolean reflected IDL attributes: present/absent, never the string
    /// "false". A loader that sets <c>s.async = false</c> to keep injected scripts in order relies on
    /// the removal half.
    /// </summary>
    internal static readonly (string IdlName, string AttributeName)[] ScriptReflectedBooleans =
    [
        ("async", "async"),
        ("defer", "defer"),
        ("noModule", "nomodule"),
    ];

    /// <summary>
    /// HTMLImageElement's plain reflected DOMString IDL attributes (HTML §4.8.3), as IDL name →
    /// content-attribute name. <c>src</c> is URL-typed and wired separately, <c>isMap</c> is boolean,
    /// and <c>width</c>/<c>height</c> report the used dimension rather than the raw attribute.
    /// <c>crossOrigin</c>, <c>decoding</c>, <c>loading</c>, <c>fetchPriority</c> and
    /// <c>referrerPolicy</c> are enumerated in the IDL and so read back a limited value in a browser;
    /// they reflect plainly here, which is what a page setting them expects and what the content
    /// attribute they write carries.
    /// </summary>
    internal static readonly (string IdlName, string AttributeName)[] ImageReflectedAttributes =
    [
        ("alt", "alt"),
        ("srcset", "srcset"),
        ("sizes", "sizes"),
        ("useMap", "usemap"),
        ("crossOrigin", "crossorigin"),
        ("referrerPolicy", "referrerpolicy"),
        ("decoding", "decoding"),
        ("loading", "loading"),
        ("fetchPriority", "fetchpriority"),
    ];

    /// <summary>
    /// A plainly reflected content attribute as its IDL getter answers it: the attribute's value, or
    /// the empty string when it is absent.
    /// </summary>
    /// <remarks>
    /// The shape these per-tag getters were each written out in, once, now that the realm mints them —
    /// <see cref="TryGetAttribute"/> is the bridge's engine-neutral scan and the answer was always the
    /// same two cases. Never JavaScript <c>null</c>: a missing reflected DOMString is <c>""</c>, which
    /// is why the empty string is spelled out rather than left to <see cref="JsValue.String(string?)"/>.
    /// </remarks>
    internal static JsValue ReflectedAttribute(Broiler.Dom.DomElement element, string attribute) =>
        JsValue.String(TryGetAttribute(element, attribute, out var value) ? value : string.Empty);
}
