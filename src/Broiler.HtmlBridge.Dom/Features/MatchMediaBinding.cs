using Broiler.CSS.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// <c>window.matchMedia(query)</c>, co-located as an HtmlBridge feature module (Phase 3). It
/// evaluates the query against the current viewport via the canonical
/// <see cref="CssStyleEngine.MatchesMediaQuery"/> and returns a <c>MediaQueryList</c>-shaped object
/// (<c>matches</c>/<c>media</c> plus no-op legacy <c>addListener</c>/<c>removeListener</c> stubs).
/// The only bridge coupling is the live viewport, reached through the narrow
/// <see cref="IMatchMediaHost"/> contract. Previously the bridge's
/// <c>JsRegistrationMatchMedia069Core</c> in the shared JsFunctionCallbacks/Registration.cs grab-bag,
/// with its media-query evaluation in the (now removed) <c>DomBridge.EvaluateMediaQuery</c> wrapper.
/// </summary>
/// <remarks>
/// The JavaScript vocabulary is JSEAL's (<see cref="IJsRealm"/>): the realm arrives on the call frame,
/// so nothing here names an engine type. The media-query evaluation never was engine-coupled.
/// </remarks>
internal static class MatchMediaBinding
{
    public static JsValue MatchMedia(IMatchMediaHost host, in JsCall call)
    {
        var realm = call.Realm;

        // An empty query parses to an empty media-query list, which is equivalent
        // to `all` and therefore matches — the evaluator handles that itself, so
        // the empty string is passed straight through rather than short-circuited.
        //
        // ToJsString, not the handle's rendering: `matchMedia(obj)` coerces its argument the way the
        // language does, so an object argument runs the `toString` the page gave it.
        var query = call.Length > 0 ? realm.ToJsString(call[0]) : string.Empty;
        var matches = CssStyleEngine.MatchesMediaQuery(
            query,
            new CssEnvironment(host.ViewportWidth, host.ViewportHeight));

        var result = realm.NewObject();
        realm.DefineValue(result, "matches", JsValue.Boolean(matches));
        realm.DefineValue(result, "media", JsValue.String(query));
        // addListener / removeListener stubs (the legacy MediaQueryList API) — no-ops.
        realm.DefineValue(result, "addListener", NoOp(realm, "addListener"));
        realm.DefineValue(result, "removeListener", NoOp(realm, "removeListener"));

        // CSSOM View §4.2 made MediaQueryList an EventTarget, and that is the pair current code
        // registers with — the two above are the deprecated spelling kept for old callers. Having
        // only the old pair is not a smaller surface but a broken one: `mql.addEventListener` is
        // then undefined, and calling it throws "undefined is not a function" *inside* whatever
        // was setting up a responsive behaviour, abandoning the rest of that setup. On
        // www.mediawiki.org that is Vector's pinnable-element code, which registers for viewport
        // changes before it decides whether the appearance panel belongs in the header or in the
        // page column — so the panel stayed in the column and pushed the article down.
        //
        // A capture renders one frame at a fixed viewport, so no `change` event can ever fire and
        // the listener is genuinely never called; what matters is that registering one is not an
        // error. `dispatchEvent` reports false — nothing was dispatched — for the same reason.
        realm.DefineValue(result, "addEventListener", NoOp(realm, "addEventListener"));
        realm.DefineValue(result, "removeEventListener", NoOp(realm, "removeEventListener"));
        realm.DefineValue(
            result,
            "dispatchEvent",
            realm.NewConstructor("dispatchEvent", static (in _) => JsValue.False, 1));
        realm.DefineValue(result, "onchange", JsValue.Null);

        return result;
    }

    /// <summary>
    /// One of the four inert listener-registration members.
    /// </summary>
    /// <remarks>
    /// <b>Constructable, which a WebIDL operation should not be — and it is kept that way because
    /// this is a refactor.</b> These five members were built with the engine's ordinary function
    /// constructor rather than with the bridge's non-constructable helper, so each carries a
    /// <c>prototype</c> object and <c>new mql.addListener()</c> answers an object where a browser
    /// throws. <see cref="IJsValues.NewConstructor"/> is the mapping that preserves that exactly;
    /// tightening the five to <see cref="IJsValues.NewMethod"/> is a real fix and belongs in its own
    /// commit, not smuggled in under a vocabulary change.
    /// </remarks>
    private static JsValue NoOp(IJsRealm realm, string name) =>
        realm.NewConstructor(name, static (in _) => JsValue.Undefined, 1);
}
