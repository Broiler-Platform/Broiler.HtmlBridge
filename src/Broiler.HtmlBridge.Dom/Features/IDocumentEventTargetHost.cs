using Broiler.Dom;
using Broiler.JSeal;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="DocumentEventTargetBinding"/> needs from the bridge: the
/// document node (the EventTarget), its per-type listener store, and the shared event-dispatch
/// algorithm.
/// </summary>
/// <remarks>
/// <para>
/// The contract names no engine type, and neither does anything behind it. Listener registration is
/// not a member here: <see cref="DocumentEventTargetBinding"/> performs those operations itself,
/// with the realm its call frame carries.
/// </para>
/// <para>
/// <see cref="DispatchEvent"/> answers the "not cancelled" boolean <c>dispatchEvent</c> is specified
/// to return, which is the whole of what the dispatch produces for a caller.
/// </para>
/// </remarks>
internal interface IDocumentEventTargetHost
{
    DomNode DocumentNode { get; }

    Dictionary<string, List<EventListenerRegistration>> GetEventListeners(DomNode node);

    JsValue DispatchEvent(DomNode target, JsValue evt);
}
