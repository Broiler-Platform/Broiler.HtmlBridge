using Broiler.HtmlBridge;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A JSEAL realm for the unit tests whose subject holds handles but does not care which engine
/// minted them: the runtime registries, the event loop's queues.
/// </summary>
/// <remarks>
/// These tests used to construct the engine's objects directly (<c>new JSObject()</c>) and live in
/// the Broiler aggregate repository, where they stopped compiling when the registries moved onto
/// <see cref="JsValue"/>. A realm is the one way to mint a handle that is an object, and the
/// Broiler.JS provider is registered in every configuration this suite builds, so it is the one
/// asked for. The theories that do care about the engine (<c>SubWindowMapKeyTests</c>,
/// <c>JsealConformanceTests</c>) enumerate <see cref="JsEngineRegistry.All"/> instead.
/// </remarks>
internal static class TestRealm
{
    static TestRealm() => JsEngineHosting.EnsureRegistered();

    /// <summary>A fresh Broiler.JS realm; the caller disposes it.</summary>
    internal static IJsRealm Create() =>
        JsEngineRegistry.Find("broiler-js")!.CreateRealm(JsRealmOptions.Default);

    /// <summary>A native function that runs <paramref name="onCall"/> and returns <c>undefined</c>.</summary>
    internal static JsValue Callback(this IJsRealm realm, Action onCall, string name = "cb") =>
        realm.NewMethod(name, (in JsCall _) =>
        {
            onCall();
            return JsValue.Undefined;
        });
}
