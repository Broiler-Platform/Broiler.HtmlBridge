using Broiler.HtmlBridge.Jseal;

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
/// <see cref="JsEngineRegistry"/> and the contracts. <c>ScriptEngine</c>'s document-free entry points
/// adopt the context they build the same way, through <see cref="DomBridgeHostUtils.AdoptRealm"/>, because they have no
/// bridge to do it for them.
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
}
