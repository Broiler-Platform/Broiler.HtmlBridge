using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using static Broiler.HtmlBridge.DomBridgeUtils;

namespace Broiler.HtmlBridge;

// Explicit ISelectorsHost implementation for the SelectorsBinding feature module (Phase 3): the bridge
// exposes selector validation, the descendant selector search, the two live element collections and the
// JS-wrapper factory via explicit interface members (the search and the two collection walks call static
// helpers that take the bridge; validation, matching and the wrapper factory forward to instance
// members), so the module reaches no arbitrary bridge private field and the public surface is unchanged.
//
// The seam this file used to be is gone: DomBridge/Utilities.cs and DomCollectionBinding both speak
// JSEAL now, so the searches, the collection factory and the wrapper lookups below are the realm's and
// nothing here converts anything. The two `HTMLCollection` builders that moved here from the module
// stay put — a live collection's named getter runs on every property read, and moving it back into the
// module, which is the same assembly, would buy nothing now that both sides hold the same handles.
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
}
