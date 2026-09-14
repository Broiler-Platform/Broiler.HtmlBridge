using Broiler.HtmlBridge.Jseal;
using Broiler.JavaScript.Engine;

namespace Broiler.HtmlBridge;

/// <summary>
/// The bridge's JSEAL realm: the engine-neutral half of the seam that <c>_jsContext</c> is the
/// engine-specific half of.
/// </summary>
/// <remarks>
/// <para>
/// <b>Both halves name the same realm.</b> Every binding builds its objects through
/// <see cref="Realm"/>, except the array <c>SetAdoptedStyleSheets</c> copies in engine terms
/// (see <c>Runtime/JsInterop.cs</c>). <c>_jsContext</c> has two readers left:
/// <c>SyncWindowMembersOntoGlobal</c>, which swaps the context's code cache around the window mirror,
/// and the sub-document module tail in <c>DomBridge/SubDocuments.cs</c>, which runs module roots on
/// it when it is a module context. (This said a binding that had not migrated still built its
/// objects on the field.) While those and <c>RegisterDocument</c>'s own cache swap need the context,
/// <c>IDomBridgeRuntime.Attach</c> takes a <c>JSContext</c> rather than an <see cref="IJsRealm"/>;
/// changing that is the change this whole layer exists to make possible.
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
    /// bindings' objects nowhere and register a document missing every global they install — a page
    /// loading with no <c>console</c> and no error. (This said "whichever globals had already moved".)
    /// The diagnosis is short and worth stating in the message: nothing linked an engine provider.
    /// </para>
    /// </remarks>
    /// <param name="options">
    /// What the page is allowed to do, read from its Content-Security-Policy by the caller. An
    /// adopted realm is bound by this exactly as a created one is; it used to be bound by nothing.
    /// </param>
    private static IJsRealm AdoptRealm(JSContext context, JsRealmOptions options)
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
}
