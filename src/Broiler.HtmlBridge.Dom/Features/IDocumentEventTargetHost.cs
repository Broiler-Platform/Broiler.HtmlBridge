using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="DocumentEventTargetBinding"/> needs from the bridge: the
/// document node (the EventTarget), its per-type listener store, and the shared event-dispatch
/// algorithm.
/// </summary>
/// <remarks>
/// <para>
/// The contract names no engine type, and neither does anything behind it now. It used to carry two
/// registration members that existed only because the <c>EventListenerRegistration</c> record held an
/// engine value: they were where a handle became one, and this remark said they would collapse into
/// <see cref="EventListenerBinding"/> when the record moved. The record has moved and they have
/// collapsed: <see cref="DocumentEventTargetBinding"/> calls those operations itself, with the realm
/// its call frame carries.
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
