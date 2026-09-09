using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit ISelectorsHost implementation for the SelectorsBinding feature module (Phase 3): the bridge
// exposes selector validation, the descendant selector search, the two live element collections and the
// JS-wrapper factory via explicit interface members (each forwards to the corresponding static/instance
// helper, passing the bridge in), so the module reaches no arbitrary bridge private field and the public
// surface is unchanged.
//
// The seam this file used to be is gone: DomBridge/Utilities.cs and DomCollectionBinding both speak
// JSEAL now, so the searches, the collection factory and the wrapper lookups below are the realm's and
// nothing here converts anything. The two `HTMLCollection` builders that moved here from the module
// stay put — a live collection's named getter runs on every property read, and pushing it back across
// an assembly boundary would buy nothing now that both sides hold the same handles.
public sealed partial class DomBridge : Dom.Features.ISelectorsHost
{
    void Dom.Features.ISelectorsHost.ValidateSelector(string selector)
        => ValidateSelector(selector);

    JsValue Dom.Features.ISelectorsHost.FindInDescendants(DomElement element, string selector, bool all)
        => FromEngineResult(FindInDescendants(element, selector, all, this));

    JsValue Dom.Features.ISelectorsHost.ElementsByTagName(DomElement element, string tagName)
        => FromEngineResult(LiveCollection(() =>
        {
            var results = new List<JsValue>();
            CollectDescendantsByTag(element, tagName, results, this);
            return results;
        }));

    JsValue Dom.Features.ISelectorsHost.ElementsByClassName(DomElement element, string classNames)
        => FromEngineResult(LiveCollection(() =>
        {
            var results = new List<JsValue>();
            CollectDescendantsByClass(element, classNames, results, this);
            return results;
        }));

    JsValue Dom.Features.ISelectorsHost.ToWrapper(DomNode node) => WrapNode(node);

    bool Dom.Features.ISelectorsHost.MatchesSelector(DomElement element, string selector, DomElement? scope)
        => MatchesSelector(element, selector, scope);

    /// <summary>
    /// An <c>HTMLCollection</c> over <paramref name="contents"/>, with the named getter DOM
    /// §4.2.10.2 gives one: a lookup answers the first element whose <c>id</c> — or, for the
    /// elements HTML names, whose <c>name</c> — matches.
    /// </summary>
    private JsValue LiveCollection(Func<List<JsValue>> contents) =>
        Dom.Features.DomCollectionBinding.HtmlCollection(Realm, contents, name => NamedItem(Realm, contents, name));

    private static JsValue? NamedItem(IJsRealm realm, Func<List<JsValue>> contents, string name)
    {
        if (name.Length == 0)
            return null;

        foreach (var candidate in contents())
        {
            if (candidate.IsObject &&
                (Matches(realm, candidate, "id", name) || Matches(realm, candidate, "name", name)))
                return candidate;
        }

        return null;

        // The attribute has to be a JavaScript *string* to match, exactly as before: an element whose
        // reflected `id` is anything else does not answer the named getter. So this reads the property
        // and tests its kind rather than coercing — realm.ToJsString would run a page toString and
        // make an object match a name it never had.
        static bool Matches(IJsRealm realm, JsValue wrapper, string attribute, string name) =>
            realm.GetProperty(wrapper, attribute) is { IsString: true } value &&
            string.Equals(value.AsString, name, StringComparison.Ordinal);
    }

    /// <summary>
    /// A selector result normalised to what the DOM says it can be: an object or JavaScript
    /// <c>null</c> and never anything else — a wrapper, a <c>NodeList</c>, an <c>HTMLCollection</c>,
    /// or the <c>null</c> a <c>querySelector</c> that matched nothing answers.
    /// </summary>
    /// <remarks>
    /// It stopped being a conversion when the searches migrated, but it stays a filter: the arms it
    /// guards all answer an object or <c>null</c> already, and this is where that invariant is
    /// stated. <c>DomBridge/JsObjects.NonElementNodes.cs</c> is the other caller.
    /// </remarks>
    private static JsValue FromEngineResult(JsValue value) => value.IsObject ? value : JsValue.Null;
}
