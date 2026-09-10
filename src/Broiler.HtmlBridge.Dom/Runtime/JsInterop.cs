using Broiler.JavaScript.BuiltIns.Array;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Runtime;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Jseal.Providers;

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
/// design rule, and it is what keeps wrapper identity and <c>el === el</c> working across a
/// half-migrated bridge.
/// </para>
/// <para>
/// <b>The weak tables are no longer among the things it keeps working, and the paragraph that used
/// to count them here was wrong twice.</b> It said the reference-key floor was three tables and
/// "a third the size the number implied"; the tree had six, because three more were typed
/// <c>&lt;object, …&gt;</c> and fed by an <c>IdentityOf</c> that unwrapped through this class. All
/// six now key on <see cref="JsValue.ObjectIdentity"/> — the reference the handle carries, which
/// every provider already makes canonical per object because handle equality is defined by it — and
/// <c>Runtime/JsObjectRegistry.cs</c> is the only per-object table left that still names an engine
/// type, because re-typing its surface is one commit across ten files rather than a step. There is
/// no reference-key floor. So this
/// is a cast, and the assertion it makes is that the realm the bridge
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
    /// <remarks>
    /// <para>
    /// <b>It answers the kind the provider would have answered, and it used to answer
    /// <see cref="JsValueKind.Object"/> for everything.</b> <c>JsProviderValue.Object</c> is the
    /// wrapper for an engine object that is <em>neither callable nor an Array exotic</em> — its own
    /// summary says so — while the provider's <c>BroilerJsMarshal.Wrap</c> tests for
    /// <c>JSFunction</c> and <c>JSArray</c> first. So a handle minted here and a handle minted by the
    /// provider over the same object disagreed about kind, and <see cref="JsValue"/> compares kind
    /// <em>before</em> reference: <c>a == b</c> was false for two handles on one object.
    /// </para>
    /// <para>
    /// <b>Nothing observed it yet, and the migration is what would have.</b> The two places that
    /// could see it are guarded on the engine side — <c>window.frames</c> goes back through
    /// <c>Unwrap</c>, which is kind-independent, and the event-dispatch path tests
    /// <c>is JSFunction</c> before wrapping. What is not guarded is a map keyed on a handle:
    /// <c>EventTargetRegistry</c>'s dictionaries are correct today only because every key it holds is
    /// kind <c>Object</c>, which is exactly the invariant that ends when a listener record becomes a
    /// handle. Fixing it here, before anything depends on it, is the cheap order.
    /// </para>
    /// <para>
    /// The test order matters and mirrors the provider's: <c>JSArray</c> derives from
    /// <c>JSObject</c>, and <c>JSFunction</c> is callable, so a plain <c>JSObject</c> arm placed
    /// first would swallow both.
    /// </para>
    /// </remarks>
    internal static JsValue FromEngineObject(JSObject value) => value switch
    {
        JSFunction function => JsProviderValue.Function(function),
        JSArray array => JsProviderValue.Array(array),
        _ => JsProviderValue.Object(value),
    };
}
