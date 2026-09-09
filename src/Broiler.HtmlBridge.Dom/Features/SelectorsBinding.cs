using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Phase 3 feature module for the DOM <c>Element</c> selector API — <c>querySelector</c>,
/// <c>querySelectorAll</c>, <c>matches</c>, <c>closest</c> and <c>getElementsByTagName</c>. These were the
/// bridge's <c>JsJsObjectsQuerySelector126Core</c>..<c>Closest129Core</c> and
/// <c>GetElementsByTagName133Core</c> callbacks; selector validation, the descendant selector search, the
/// two live element collections and the JS-wrapper factory reach the bridge through
/// <see cref="ISelectorsHost"/>, while selector matching (<c>MatchesSelector</c>) is a host member too
/// and the element-parent walk (<c>ParentEl</c>) is the bridge's <c>internal static</c> helper, called
/// directly.
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>), so nothing here names an engine type.
/// The one thing that moved <em>out</em> is the argument read: each entry point takes the string its
/// caller has already produced, because the registration site in <c>DomBridge.ElementInterface.cs</c>
/// has not migrated and its call frame is still an engine one. The coercion is unchanged — it is the
/// same ECMAScript <c>ToString</c> on the same argument — and it moves back in here as
/// <c>call.Realm.ToJsString(call[0])</c> when that site migrates.
/// </remarks>
internal static class SelectorsBinding
{
    /// <summary>
    /// The selector argument of the four <c>Element</c> selector methods, validated per DOM §4.2.6
    /// before any matching happens — an unparsable one is a <c>SyntaxError</c>, not an empty result.
    /// </summary>
    /// <remarks>
    /// All four validate, because a browser throws from all four identically; that was measured, not
    /// inferred from the fact that they share an algorithm. For the two <c>querySelector</c> forms
    /// this repeats a check the shared descendant search makes as well, since that search is also
    /// reached directly by the <c>DocumentFragment</c> forms and has to stand on its own; the second
    /// scan of a short string is not worth removing the redundancy for.
    /// </remarks>
    private static string Selector(ISelectorsHost host, string selector)
    {
        host.ValidateSelector(selector);
        return selector;
    }

    public static JsValue QuerySelector(ISelectorsHost host, DomElement element, string selector) =>
        host.FindInDescendants(element, Selector(host, selector), false);

    public static JsValue QuerySelectorAll(ISelectorsHost host, DomElement element, string selector) =>
        host.FindInDescendants(element, Selector(host, selector), true);

    public static JsValue Matches(ISelectorsHost host, DomElement element, string selector)
    {
        var sel = Selector(host, selector);
        return JsValue.Boolean(
            !DomApiSyntax.CarriesPseudoElement(sel) && host.MatchesSelector(element, sel, element));
    }

    public static JsValue Closest(ISelectorsHost host, DomElement element, string selector)
    {
        var sel = Selector(host, selector);
        if (DomApiSyntax.CarriesPseudoElement(sel))
            return JsValue.Null;

        for (DomElement? current = element; current != null && !current.TagName.StartsWith('#'); current = DomBridge.ParentEl(current))
        {
            if (host.MatchesSelector(current, sel, element))
                return host.ToWrapper(current);
        }

        return JsValue.Null;
    }

    /// <summary>
    /// <c>element.getElementsByTagName(name)</c> — a <b>live</b> <c>HTMLCollection</c> (DOM §4.9). It
    /// was a snapshot array, so the loop this method exists for —
    /// <c>for (var i = 0; i &lt; items.length; i++)</c> over a list the body mutates — walked a
    /// different collection than a browser walks.
    /// </summary>
    public static JsValue GetElementsByTagName(ISelectorsHost host, DomElement element, string name) =>
        host.ElementsByTagName(element, name.ToLowerInvariant());

    /// <summary>
    /// <c>element.getElementsByClassName(names)</c> — DOM §4.9 defines it on <c>Element</c> as well as
    /// on <c>Document</c>, and only the document half was registered. Missing on an element it did not
    /// read as absent: calling it threw <c>TypeError: undefined is not a function</c>, which aborts the
    /// whole script. google.com's One-Google-bar bundle scopes its lookups to a container that way —
    /// <c>d.getElementsByClassName("gb_C")[0]||d</c> — so it died there.
    /// </summary>
    public static JsValue GetElementsByClassName(ISelectorsHost host, DomElement element, string classNames) =>
        host.ElementsByClassName(element, classNames);
}
