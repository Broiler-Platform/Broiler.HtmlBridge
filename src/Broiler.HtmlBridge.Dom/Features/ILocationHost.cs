using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="LocationBinding"/> needs from the bridge: somewhere to record
/// a cross-document navigation the page asked for, and the window-scoped dispatch a same-document
/// one fires <c>hashchange</c> through.
/// <para>
/// The two halves are the two kinds of navigation. A fragment navigation is not a load, so the
/// binding performs it and only needs to announce it — that is
/// <see cref="DispatchWindowEvent"/>. Everything else needs a loader the binding does not have, so
/// it is recorded and left to the host — that is <see cref="RequestNavigation"/>.
/// </para>
/// </summary>
internal interface ILocationHost
{
    JSValue DispatchWindowEvent(JSObject evt);

    void RequestNavigation(NavigationRequest request);
}
