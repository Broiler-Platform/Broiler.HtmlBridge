using Broiler.VM.Profile.JavaScript;

namespace Broiler.HtmlBridge.Jseal.Vm;

/// <summary>
/// What the composition registers so the profile hands its realm over, and how a turn is taken.
/// </summary>
/// <remarks>
/// <para>
/// <b>It does nothing at realm creation, which is the opposite of what an embedder usually does
/// with this callback.</b> The profile's host surface is designed for a composition that installs
/// its own object model the moment a realm exists; a JSEAL provider installs nothing, because what
/// goes into the realm is the DOM bridge's business and it has not been asked yet. So this captures
/// the realm and returns, and every later crossing arrives either inside a step already or through
/// a turn.
/// </para>
/// <para>
/// <b>The pending action is one deep and cleared before it runs.</b> A turn runs one crossing,
/// because a crossing is what asked for the turn; clearing first means an action that itself asks
/// for a turn - which it cannot, being already in a step - could not re-enter this one.
/// </para>
/// </remarks>
internal sealed class VmHostBridge : IJsHostSurface
{
    /// <summary>The realm the profile handed over, or null before instantiation completed.</summary>
    internal JsHostRealm? Realm { get; private set; }

    /// <summary>
    /// The realm's <c>Promise</c>, taken before any page script could replace it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It is captured here because <c>Promise</c> is an ordinary writable global and a page may
    /// assign over it.</b> The profile installs it <c>Writable | Configurable</c>, as the language
    /// requires — so a provider that read <c>globalThis.Promise</c> at the moment the bridge wanted
    /// a promise would hand a page's own constructor whatever <c>fetch</c> was about to resolve,
    /// and a page that had replaced it would receive every deferred result the bridge produces.
    /// Reading it once, before any guest program has run, is what makes <c>NewPromise</c> answer
    /// with the intrinsic.
    /// </para>
    /// <para>
    /// <b>This is deliberately the opposite of what <c>DomError</c> does with <c>DOMException</c>,
    /// and the difference is who owns the global.</b> <c>DOMException</c> is the bridge's to install
    /// and does not exist yet when a realm is created, so resolving it late is the only way to find
    /// it. <c>Promise</c> is the realm's own and exists before anyone else can touch it, so
    /// resolving it late is the only way to lose it.
    /// </para>
    /// </remarks>
    internal JsHostValue Promise { get; private set; }

    /// <summary>The one crossing waiting for a step.</summary>
    internal Action<JsHostRealm>? Pending { get; set; }

    /// <inheritdoc />
    /// <remarks>
    /// This runs inside a step the profile opens at instantiation, so the crossing below is legal
    /// here and would not be from anywhere else this class is reachable from.
    /// </remarks>
    public void OnRealmCreated(JsHostRealm realm)
    {
        Realm = realm;
        Promise = realm.GetProperty(realm.Global, "Promise");
    }

    /// <inheritdoc />
    public void OnTurn(JsHostRealm realm)
    {
        var work = Pending;
        Pending = null;
        work?.Invoke(realm);
    }
}
