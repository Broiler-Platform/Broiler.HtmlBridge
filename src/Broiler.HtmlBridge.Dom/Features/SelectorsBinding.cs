using System;
using System.Collections.Generic;
using Broiler.Dom;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Array;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// Phase 3 feature module for the DOM <c>Element</c> selector API — <c>querySelector</c>,
/// <c>querySelectorAll</c>, <c>matches</c>, <c>closest</c> and <c>getElementsByTagName</c>. These were the
/// bridge's <c>JsJsObjectsQuerySelector126Core</c>..<c>Closest129Core</c> and
/// <c>GetElementsByTagName133Core</c> callbacks; the descendant selector search, the by-tag collector and
/// the JS-wrapper factory reach the bridge through <see cref="ISelectorsHost"/>, while selector matching
/// (<c>MatchesSelector</c>) and the element-parent walk (<c>ParentEl</c>) are the bridge's
/// <c>internal static</c> helpers, called directly.
/// </summary>
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
    private static string Selector(ISelectorsHost host, in Arguments a)
    {
        var selector = a.Length > 0 ? a[0].ToString() : string.Empty;
        DomBridge.ValidateSelector(selector, host.JsContext);
        return selector;
    }

    public static JSValue QuerySelector(ISelectorsHost host, DomElement element, in Arguments a) =>
        host.FindInDescendants(element, Selector(host, in a), false);

    public static JSValue QuerySelectorAll(ISelectorsHost host, DomElement element, in Arguments a) =>
        host.FindInDescendants(element, Selector(host, in a), true);

    public static JSValue Matches(ISelectorsHost host, DomElement element, in Arguments a)
    {
        var sel = Selector(host, in a);
        return !DomApiSyntax.CarriesPseudoElement(sel) && host.MatchesSelector(element, sel, element)
            ? JSBoolean.True
            : JSBoolean.False;
    }

    public static JSValue Closest(ISelectorsHost host, DomElement element, in Arguments a)
    {
        var sel = Selector(host, in a);
        if (DomApiSyntax.CarriesPseudoElement(sel))
            return JSNull.Value;

        for (DomElement? current = element; current != null && !current.TagName.StartsWith('#'); current = DomBridge.ParentEl(current))
        {
            if (host.MatchesSelector(current, sel, element))
                return host.ToJSObject(current);
        }

        return JSNull.Value;
    }

    /// <summary>
    /// <c>element.getElementsByTagName(name)</c> — a <b>live</b> <c>HTMLCollection</c> (DOM §4.9). It
    /// was a snapshot array, so the loop this method exists for —
    /// <c>for (var i = 0; i &lt; items.length; i++)</c> over a list the body mutates — walked a
    /// different collection than a browser walks.
    /// </summary>
    public static JSValue GetElementsByTagName(ISelectorsHost host, DomElement element, in Arguments a)
    {
        var tagSearch = a.Length > 0 ? a[0].ToString().ToLowerInvariant() : string.Empty;
        return LiveCollection(host, () =>
        {
            var results = new List<JSValue>();
            host.CollectElementsByTagName(element, tagSearch, results);
            return results;
        });
    }

    /// <summary>
    /// An <c>HTMLCollection</c> over <paramref name="contents"/>, with the named getter DOM
    /// §4.2.10.2 gives one: a lookup answers the first element whose <c>id</c> — or, for the
    /// elements HTML names, whose <c>name</c> — matches.
    /// </summary>
    private static JSValue LiveCollection(ISelectorsHost host, Func<List<JSValue>> contents) =>
        DomCollectionBinding.HtmlCollection(host.JsContext, contents, name => NamedItem(host, contents, name));

    private static JSValue? NamedItem(ISelectorsHost host, Func<List<JSValue>> contents, string name)
    {
        if (name.Length == 0)
            return null;

        foreach (var candidate in contents())
        {
            if (candidate is JSObject wrapper &&
                (Matches(wrapper, "id", name) || Matches(wrapper, "name", name)))
                return wrapper;
        }

        return null;

        static bool Matches(JSObject wrapper, string attribute, string name) =>
            wrapper[(KeyString)attribute] is JSString value &&
            string.Equals(value.ToString(), name, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>element.getElementsByClassName(names)</c> — DOM §4.9 defines it on <c>Element</c> as well as
    /// on <c>Document</c>, and only the document half was registered. Missing on an element it did not
    /// read as absent: calling it threw <c>TypeError: undefined is not a function</c>, which aborts the
    /// whole script. google.com's One-Google-bar bundle scopes its lookups to a container that way —
    /// <c>d.getElementsByClassName("gb_C")[0]||d</c> — so it died there.
    /// </summary>
    public static JSValue GetElementsByClassName(ISelectorsHost host, DomElement element, in Arguments a)
    {
        var classNames = a.Length > 0 ? a[0].ToString() : string.Empty;
        return LiveCollection(host, () =>
        {
            var results = new List<JSValue>();
            host.CollectElementsByClassName(element, classNames, results);
            return results;
        });
    }
}
