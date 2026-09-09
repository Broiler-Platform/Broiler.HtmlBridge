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

    /// <summary>The one crossing waiting for a step.</summary>
    internal Action<JsHostRealm>? Pending { get; set; }

    /// <inheritdoc />
    public void OnRealmCreated(JsHostRealm realm) => Realm = realm;

    /// <inheritdoc />
    public void OnTurn(JsHostRealm realm)
    {
        var work = Pending;
        Pending = null;
        work?.Invoke(realm);
    }
}
