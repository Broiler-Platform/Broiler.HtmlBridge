using Broiler.CSS.Dom;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.BuiltIns.Boolean;
using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.String;

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
internal static class MatchMediaBinding
{
    public static JSValue MatchMedia(IMatchMediaHost host, in Arguments a)
    {
        // An empty query parses to an empty media-query list, which is equivalent
        // to `all` and therefore matches — the evaluator handles that itself, so
        // the empty string is passed straight through rather than short-circuited.
        var query = a.Length > 0 ? a[0].ToString() : string.Empty;
        var matches = CssStyleEngine.MatchesMediaQuery(
            query,
            new CssEnvironment(host.ViewportWidth, host.ViewportHeight));

        var result = new JSObject();
        result.FastAddValue("matches", matches ? JSBoolean.True : JSBoolean.False, JSPropertyAttributes.EnumerableConfigurableValue);
        result.FastAddValue("media", new JSString(query), JSPropertyAttributes.EnumerableConfigurableValue);
        // addListener / removeListener stubs (the legacy MediaQueryList API) — no-ops.
        result.FastAddValue("addListener", NoOp("addListener"), JSPropertyAttributes.EnumerableConfigurableValue);
        result.FastAddValue("removeListener", NoOp("removeListener"), JSPropertyAttributes.EnumerableConfigurableValue);

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
        result.FastAddValue("addEventListener", NoOp("addEventListener"), JSPropertyAttributes.EnumerableConfigurableValue);
        result.FastAddValue("removeEventListener", NoOp("removeEventListener"), JSPropertyAttributes.EnumerableConfigurableValue);
        result.FastAddValue(
            "dispatchEvent",
            new JSFunction((in _) => JSBoolean.False, "dispatchEvent", 1),
            JSPropertyAttributes.EnumerableConfigurableValue);
        result.FastAddValue("onchange", JSNull.Value, JSPropertyAttributes.EnumerableConfigurableValue);

        return result;
    }

    private static JSFunction NoOp(string name) => new((in _) => JSUndefined.Value, name, 1);
}
