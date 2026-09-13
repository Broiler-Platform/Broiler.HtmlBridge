namespace Broiler.HtmlBridge.Jseal;

/// <summary>
/// An optional provider capability: wrapping a realm the host already built with the engine's own
/// API, rather than one <see cref="IJsEngineProvider.CreateRealm"/> made.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists for the migration and should outlive it by not very long.</b> The DOM bridge is
/// 250 files of engine-specific binding code, and it cannot become engine-neutral in one commit.
/// While it is half migrated, one <c>JSContext</c> has to serve both halves: the bindings already
/// written against <see cref="IJsRealm"/> and the ones still writing <c>JSObject</c>s directly. The
/// realm cannot be the one the provider created, because the unmigrated half was handed the context
/// by <c>ScriptEngine</c> and builds on it directly.
/// </para>
/// <para>
/// Declaring it as a provider capability rather than a static factory on the provider assembly is what
/// keeps the bridge from having to reference an engine to get one: the bridge asks
/// <see cref="JsEngineRegistry"/> for the default provider and asks that whether it can adopt what the
/// host handed over. So the assembly whose engine coupling is being counted down does not gain a
/// reference on the way.
/// </para>
/// <para>
/// A provider that cannot do this — because its engine's realms are not host-constructible, or because
/// it would have no way to tell one of its own realms from a foreign object — simply does not
/// implement the interface, and the host falls back to creating a realm of its own.
/// </para>
/// </remarks>
public interface IJsRealmAdoption
{
    /// <summary>
    /// Wraps <paramref name="engineRealm"/> when it is one of this engine's realms.
    /// </summary>
    /// <param name="engineRealm">
    /// The engine's own realm object — a <c>JSContext</c> for the Broiler.JS provider. Never trusted:
    /// an implementation type-tests it and answers <see langword="false"/> for anything else, because
    /// a host with two engines linked will offer the same object to both.
    /// </param>
    /// <param name="options">
    /// The realm's policy, which an adopted realm is bound by exactly as a created one is.
    /// </param>
    /// <param name="realm">The adopted realm, or <see langword="null"/>.</param>
    /// <returns>Whether the object was this engine's and has been adopted.</returns>
    /// <remarks>
    /// <para>
    /// The adopted realm does <b>not</b> own what it wraps: disposing it must not dispose the
    /// underlying realm, because the host that created it will. Adopting the same object twice may
    /// return two <see cref="IJsRealm"/> instances, so a caller that needs one keeps the one it got.
    /// </para>
    /// <para>
    /// <b><paramref name="options"/> is a parameter and not a default because an adopted realm used
    /// to have no policy at all.</b> The implementation hardcoded a permissive one, so every
    /// capability a host could narrow was silently un-narrowed the moment the realm came from a
    /// context rather than from <see cref="IJsEngineProvider.CreateRealm"/> — which, in a browser,
    /// is every page. Adding an overload with a permissive default would have preserved exactly the
    /// defect; taking it here makes a host that adopts state what the page is allowed to do.
    /// </para>
    /// </remarks>
    bool TryAdopt(object engineRealm, JsRealmOptions options, out IJsRealm? realm);
}
