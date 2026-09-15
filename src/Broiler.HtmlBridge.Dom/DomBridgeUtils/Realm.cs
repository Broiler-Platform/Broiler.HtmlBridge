using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Scripting;
using Broiler.JavaScript.Engine;

namespace Broiler.HtmlBridge;

public static partial class DomBridgeUtils
{
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
    /// bindings' objects nowhere and register a document missing every global they install — a page
    /// loading with no <c>console</c> and no error. (This said "whichever globals had already moved".)
    /// The diagnosis is short and worth stating in the message: nothing linked an engine provider.
    /// </para>
    /// <para>
    /// It is <see langword="internal"/> because <c>ScriptEngine</c>'s two document-free entry points,
    /// <c>Execute(scripts)</c> and <c>ExecuteDetailed(scripts)</c>, adopt the context they build through it
    /// too. So there is one loop and one place a context the host built is matched to a provider, and a
    /// missing provider fails here, with this message, before any of the caller's scripts runs on either
    /// kind of path.
    /// </para>
    /// </remarks>
    /// <param name="context">
    /// The context the host built and still owns. The realm wraps it and does not dispose it.
    /// </param>
    /// <param name="options">
    /// What scripts in the realm are allowed to do, mapped by the caller from the policy that governs them
    /// (<see cref="RealmOptionsFor"/>): the bridge's <c>Csp</c> on a document path, <c>ScriptEngine.Csp</c>
    /// on a document-free one. An adopted realm is bound by this exactly as a created one is; it used to be
    /// bound by nothing.
    /// </param>
    internal static IJsRealm AdoptRealm(JSContext context, JsRealmOptions options)
    {
        foreach (var provider in JsEngineRegistry.All)
        {
            if (provider is IJsRealmAdoption adoption &&
                adoption.TryAdopt(context, options, out var realm) &&
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

    /// <summary>
    /// The realm options a policy maps to: guest evaluation is allowed exactly when
    /// <see cref="ContentSecurityPolicy.AllowsEval"/> says so, and allowed when there is no policy.
    /// </summary>
    /// <remarks>
    /// One mapping for every path that adopts a context the host built: <c>RegisterDocumentCore</c>, with
    /// the bridge's policy, and <c>ScriptEngine</c>'s two document-free entry points, with the host's.
    /// </remarks>
    internal static JsRealmOptions RealmOptionsFor(ContentSecurityPolicy? policy) =>
        new() { AllowGuestEval = policy?.AllowsEval ?? true };
}
