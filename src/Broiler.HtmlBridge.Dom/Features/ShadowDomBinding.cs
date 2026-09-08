using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The shadow-DOM JS-binding members — the <c>element.shadowRoot</c> getter and
/// <c>element.attachShadow()</c> method — registered on every element wrapper, co-located as an
/// HtmlBridge feature module (Phase 3). The getter exposes an attached root only when its mode is
/// <c>open</c>; <c>attachShadow</c> rejects a second attachment (<c>NotSupportedError</c>), normalizes
/// the requested mode to <c>open</c>/<c>closed</c>, and creates + links the root through the
/// <see cref="IShadowDomHost"/> contract — the per-element shadow linkage stays the bridge's
/// <c>ElementRuntimeState.Shadow</c> slot (reached only through named primitives, the P3.7 pattern). Was
/// the bridge's <c>JsJsObjectsGetShadowRoot019Core</c> / <c>AttachShadow087Core</c>.
/// </summary>
/// <remarks>
/// <para>
/// Both operations are spelled in JSEAL (<see cref="IJsRealm"/>) and this file names no engine type.
/// The engine call frame is gone from both: <c>shadowRoot</c> never read one, and
/// <c>attachShadow</c> reads only its options argument, which it takes as a <see cref="JsValue"/> —
/// so the registration site hands over that one argument rather than a whole frame, and the
/// <c>mode</c> read with its <c>toString</c> coercion happens here, through the realm.
/// </para>
/// <para>
/// The script context this module used to take was there for one thing — raising the
/// <c>NotSupportedError</c> a second <c>attachShadow</c> gets — and <see cref="IJsCalls.DomError"/>
/// owns that now, which is why the host contract carries a realm and nothing else engine-shaped.
/// </para>
/// </remarks>
internal static class ShadowDomBinding
{
    /// <summary>
    /// <c>element.shadowRoot</c> — the attached root, but only when it was attached <c>open</c>. A
    /// closed root reports <see cref="JsValue.Null"/>, which is the whole point of the mode.
    /// </summary>
    public static JsValue GetShadowRoot(IShadowDomHost host, DomElement element)
    {
        var shadowRoot = host.GetShadowRoot(element);
        if (shadowRoot == null)
            return JsValue.Null;
        var mode = host.TryGetShadowMode(element, out var rawMode) ? rawMode : null;
        return string.Equals(mode, "open", StringComparison.OrdinalIgnoreCase) ? host.WrapNode(shadowRoot) : JsValue.Null;
    }

    /// <summary>
    /// <c>element.attachShadow(init)</c> — one root per element, with a mode that is <c>closed</c>
    /// only when it was asked for by that name and <c>open</c> for everything else.
    /// </summary>
    /// <param name="options">
    /// The init dictionary, or anything that is not an object — including nothing passed at all,
    /// which is <see cref="JsValue.Missing"/>. Only an object carries a <c>mode</c>: that is the test
    /// the engine frame applied before this took a handle, and it is why a page calling
    /// <c>attachShadow('closed')</c> still gets an open root.
    /// </param>
    public static JsValue AttachShadow(IShadowDomHost host, DomElement element, JsValue options)
    {
        var realm = host.Realm;

        if (host.GetShadowRoot(element) != null)
            throw realm.DomError("NotSupportedError", "Shadow root already attached.");

        var mode = "open";
        if (options.IsObject)
        {
            var modeValue = realm.GetProperty(options, "mode");
            if (!modeValue.IsNullish)
            {
                // The observable ECMAScript coercion, not the handle's diagnostic rendering: an
                // object with a toString is a legal `mode`, and what it stringifies to is the answer.
                mode = realm.ToJsString(modeValue);
            }
        }

        mode = string.Equals(mode, "closed", StringComparison.OrdinalIgnoreCase) ? "closed" : "open";
        var shadowRoot = host.AttachShadowRoot(element, mode);
        return host.WrapNode(shadowRoot);
    }
}
