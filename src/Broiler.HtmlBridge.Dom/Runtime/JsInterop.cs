using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Jseal.Providers;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Runtime;

/// <summary>
/// The boundary between the part of the DOM bridge that has been migrated to JSEAL and the part that
/// has not.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type is scaffolding, and it is meant to be deleted.</b> The bridge is 250 engine-coupled
/// files and cannot become engine-neutral in one commit; while it is half migrated, a binding written
/// against <see cref="IJsRealm"/> hands back a <see cref="JsValue"/> to a caller that still holds a
/// <c>JSObject</c>, and something has to sit between them. Every use of this class is one such seam,
/// which is why <c>eng/jseal-budget.json</c> counts them: the number is the length of the remaining
/// migration, and it may only fall.
/// </para>
/// <para>
/// <b>It costs nothing at run time and it is not a conversion.</b> Under the Broiler.JS provider a
/// JSEAL object handle carries the engine's own <c>JSObject</c> — that is the provider's central
/// design rule, and it is what keeps wrapper identity, the seven
/// <c>ConditionalWeakTable&lt;JSObject, …&gt;</c> keyed on it, and <c>el === el</c> all working across
/// a half-migrated bridge. So this is a cast, and the assertion it makes is that the realm the bridge
/// is attached to is a Broiler.JS realm. On a build serving a different engine it would fail loudly at
/// the first migrated binding, which is correct: the unmigrated half of the bridge cannot run on
/// another engine, and finding that out at the seam is better than producing an object nothing can
/// use.
/// </para>
/// <para>
/// Nothing here reaches for a provider assembly. <c>JsProviderValue</c> lives in JSEAL itself, and the
/// engine types it hands back are ones this project already references — so migrating a file lowers
/// this project's engine-reference count without a new dependency arriving to replace it.
/// </para>
/// </remarks>
internal static class JsInterop
{
    /// <summary>
    /// The engine object behind a JSEAL handle, for an unmigrated caller that needs one.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The handle does not carry a Broiler.JS object — either it is a primitive, or the realm belongs
    /// to a different engine. Both mean the caller cannot do what it was about to do.
    /// </exception>
    internal static JSObject ToEngineObject(JsValue value) =>
        JsProviderValue.ReferenceOf(value) as JSObject
        ?? throw new InvalidOperationException(
            $"A JSEAL handle of kind {value.Kind} does not carry a Broiler.JS object. A migrated " +
            "binding produced a value the unmigrated half of the bridge cannot hold; either the realm " +
            "is not a Broiler.JS realm, or the binding returned a primitive where an object was expected.");

    /// <summary>The engine value behind a JSEAL handle, or <see langword="null"/> for a primitive.</summary>
    internal static JSValue? ToEngineValue(JsValue value) => JsProviderValue.ReferenceOf(value) as JSValue;

    /// <summary>A JSEAL handle over an engine object, for a migrated callee taking one from an unmigrated caller.</summary>
    internal static JsValue FromEngineObject(JSObject value) => JsProviderValue.Object(value);
}
