using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.JavaScript.BuiltIns.String;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge;

// Explicit ISelectorsHost implementation for the SelectorsBinding feature module (Phase 3): the bridge
// exposes selector validation, the descendant selector search, the two live element collections and the
// JS-wrapper factory via explicit interface members (each forwards to the corresponding static/instance
// helper, passing the bridge in), so the module reaches no arbitrary bridge private field and the public
// surface is unchanged.
//
// This file is the engine-typed half of the seam. The module is written against JSEAL and hands back
// JsValue handles; the searches, the collection factory and the wrapper cache below still speak
// Broiler.JS, and they stop doing so when DomBridge/Utilities.cs and DomCollectionBinding migrate. The
// two `HTMLCollection` builders moved here from the module for that reason and are otherwise unchanged:
// assembling one is entirely engine-typed work today, and reassembling it from a list handed across the
// seam would convert every element of a live collection twice on every property read.
public sealed partial class DomBridge : Dom.Features.ISelectorsHost
{
    void Dom.Features.ISelectorsHost.ValidateSelector(string selector)
        => ValidateSelector(selector, _jsContext);

    JsValue Dom.Features.ISelectorsHost.FindInDescendants(DomElement element, string selector, bool all)
        => FromEngineResult(FindInDescendants(element, selector, all, this));

    JsValue Dom.Features.ISelectorsHost.ElementsByTagName(DomElement element, string tagName)
        => FromEngineResult(LiveCollection(() =>
        {
            var results = new List<JSValue>();
            CollectDescendantsByTag(element, tagName, results, this);
            return results;
        }));

    JsValue Dom.Features.ISelectorsHost.ElementsByClassName(DomElement element, string classNames)
        => FromEngineResult(LiveCollection(() =>
        {
            var results = new List<JSValue>();
            CollectDescendantsByClass(element, classNames, results, this);
            return results;
        }));

    JsValue Dom.Features.ISelectorsHost.ToWrapper(DomNode node)
        => Dom.Runtime.JsInterop.FromEngineObject(ToJSObject(node));

    bool Dom.Features.ISelectorsHost.MatchesSelector(DomElement element, string selector, DomElement? scope)
        => MatchesSelector(element, selector, scope);

    /// <summary>
    /// An <c>HTMLCollection</c> over <paramref name="contents"/>, with the named getter DOM
    /// §4.2.10.2 gives one: a lookup answers the first element whose <c>id</c> — or, for the
    /// elements HTML names, whose <c>name</c> — matches.
    /// </summary>
    private JSValue LiveCollection(Func<List<JSValue>> contents) =>
        Dom.Features.DomCollectionBinding.HtmlCollection(_jsContext, contents, name => NamedItem(contents, name));

    private static JSValue? NamedItem(Func<List<JSValue>> contents, string name)
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
    /// A JSEAL handle over a selector result, which is an object or JavaScript <c>null</c> and never
    /// anything else: a wrapper, a <c>NodeList</c>, an <c>HTMLCollection</c>, or the <c>null</c> a
    /// <c>querySelector</c> that matched nothing answers.
    /// </summary>
    private static JsValue FromEngineResult(JSValue value) =>
        value is JSObject obj ? Dom.Runtime.JsInterop.FromEngineObject(obj) : JsValue.Null;
}
