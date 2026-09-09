using Broiler.HtmlBridge.Jseal;
using Broiler.JavaScript.Engine;

namespace Broiler.HtmlBridge;

/// <summary>
/// The bridge's JSEAL realm: the engine-neutral half of the seam that <c>_jsContext</c> is the
/// engine-specific half of.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both halves name the same realm, and that is what makes the migration incremental.</b> A
/// binding that has been migrated builds its objects through <see cref="Realm"/>; one that has not
/// still builds them on <c>_jsContext</c>. They are the same JavaScript realm, so a
/// <c>console</c> built through JSEAL and a <c>window</c> built directly can hold each other, and a
/// page cannot tell which of its globals came from which half. When the last binding moves, the field
/// below stops being a second name for a context the bridge was handed and becomes the only name it
/// has — and <c>IDomBridgeRuntime.Attach</c> can then take an <see cref="IJsRealm"/> instead of a
/// <c>JSContext</c>, which is the change this whole layer exists to make possible.
/// </para>
/// <para>
/// <b>The realm is adopted, not created.</b> <c>ScriptEngine</c> builds the <c>JSContext</c> and owns
/// its lifetime; the bridge borrows it, and has always only dropped its reference on teardown rather
/// than disposing it. Asking the registered provider to wrap what the host handed over preserves
/// exactly that ownership, and it is why this file references no provider assembly — only
/// <see cref="JsEngineRegistry"/> and the contracts.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    private IJsRealm? _realm;

    /// <summary>
    /// The realm this bridge is attached to.
    /// </summary>
    /// <exception cref="InvalidOperationException">The bridge is not attached to a context yet.</exception>
    internal IJsRealm Realm =>
        _realm ?? throw new InvalidOperationException(
            "The DOM bridge has no JavaScript realm. A binding asked for one before Attach ran, or " +
            "after the bridge was torn down.");

    /// <summary>
    /// Wraps the context the host handed over as a JSEAL realm, by asking the registered provider
    /// whether it recognises it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every provider is asked, not just the default one, because the object came from whichever
    /// engine the host chose to build it with and that need not be the engine a page load would
    /// otherwise select — a run with <c>BROILER_JS_ENGINE</c> set is exactly that case. A provider
    /// that does not recognise the object says so; see <see cref="IJsRealmAdoption"/>.
    /// </para>
    /// <para>
    /// A failure here is thrown rather than tolerated. A bridge with no realm would build its
    /// migrated bindings' objects nowhere and register a document missing whichever globals had
    /// already moved — a page loading with no <c>console</c> and no error. The diagnosis is short and
    /// worth stating in the message: nothing linked an engine provider.
    /// </para>
    /// </remarks>
    private static IJsRealm AdoptRealm(JSContext context)
    {
        foreach (var provider in JsEngineRegistry.All)
        {
            if (provider is IJsRealmAdoption adoption &&
                adoption.TryAdopt(context, out var realm) &&
                realm is not null)
            {
                return realm;
            }
        }

        throw new InvalidOperationException(
            "No registered JavaScript engine provider recognised the script context the host supplied. " +
            $"{JsEngineRegistry.All.Count} provider(s) are registered. A host must reference an engine " +
            "provider assembly — Broiler.HtmlBridge.Jseal.BroilerJs for Broiler.JS — and that assembly " +
            "registers itself when it is loaded.");
    }
}
