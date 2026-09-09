using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="LocationBinding"/> needs from the bridge: the realm the
/// <c>hashchange</c> event is minted in, somewhere to record a cross-document navigation the page asked
/// for, and the window-scoped dispatch a same-document one fires <c>hashchange</c> through.
/// <para>
/// The two navigation halves are the two kinds of navigation. A fragment navigation is not a load, so
/// the binding performs it and only needs to announce it — that is <see cref="Realm"/> plus
/// <see cref="DispatchWindowEvent"/>. Everything else needs a loader the binding does not have, so it
/// is recorded and left to the host — that is <see cref="RequestNavigation"/>.
/// </para>
/// </summary>
/// <remarks>
/// The contract names no engine type: the event is a <see cref="JsValue"/> the module builds through
/// <see cref="Realm"/>, and the dispatch result is dropped — it always was, because a <c>hashchange</c>
/// is not cancelable and there is nothing for the binding to decide from it.
/// </remarks>
internal interface ILocationHost
{
    /// <summary>The realm the <c>hashchange</c> event object is minted in.</summary>
    IJsRealm Realm { get; }

    void DispatchWindowEvent(JsValue evt);

    void RequestNavigation(NavigationRequest request);
}
