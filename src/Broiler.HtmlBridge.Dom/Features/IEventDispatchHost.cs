using Broiler.JSeal;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow bridge services the <see cref="EventDispatchBinding"/> feature module needs.
/// The capture → target → bubble dispatch
/// algorithm needs the realm the event object lives in, JS-wrapper identity, the document/window
/// global wrappers (the event path's endpoints), and read access to the per-node listener store
/// (<c>EventTargetRegistry</c>) and the inline <c>on*</c> handler for one event type — nothing
/// else. Listener registration and inline-handler compilation stay in the bridge; this module only
/// reads what it dispatches.
/// </summary>
/// <remarks>
/// <para>
/// The contract names no engine type, and nothing behind it holds one: wrappers are
/// <see cref="JsValue"/> handles and the realm is where the event object's members are installed.
/// </para>
/// <para>
/// <b><see cref="GetEventListeners"/> hands back registrations that hold handles.</b>
/// <c>EventListenerRegistration</c> stores its listener as a <see cref="JsValue"/> and the invoker
/// calls it through the realm, for this path and for the window, form-submit and messaging paths that
/// share both.
/// </para>
/// <para>
/// <b><see cref="InlineEventHandler"/> is narrower than the map it reads.</b>
/// The inline <c>on*</c> handlers are a dictionary of <see cref="JsValue"/> handles, and a
/// per-node mutable store is not something a dispatching module should be handed, so the contract asks
/// for the one handler it will fire.
/// </para>
/// <para>
/// The inherited <see cref="IDocumentNodeHost.DocumentNode"/> is the top of the propagation path.
/// </para>
/// </remarks>
internal interface IEventDispatchHost : IDocumentNodeHost, INodeWrapperHost, IRealmHost
{
    /// <summary>The JS <c>document</c> wrapper, used as the event target/currentTarget when the
    /// document node is on the path; not an object before the document global is installed.</summary>
    JsValue DocumentWrapper { get; }

    /// <summary>The JS <c>window</c> wrapper appended to <c>composedPath()</c>; not an object before
    /// the window global is installed.</summary>
    JsValue WindowWrapper { get; }

    /// <summary>The per-event-type registered listeners for <paramref name="node"/> (live store).</summary>
    Dictionary<string, List<EventListenerRegistration>> GetEventListeners(DomNode node);

    /// <summary>
    /// The compiled inline <c>on*</c> handler for <paramref name="eventType"/> on
    /// <paramref name="node"/>, or <see cref="JsValue.Missing"/> when there is none.
    /// </summary>
    /// <remarks>
    /// Answers <see cref="JsValue.Missing"/> — not the stored value — for anything that is not a
    /// callable. The test stays on the bridge's side because the store is the bridge's; it is a read of
    /// the stored handle's kind. See the remarks on this interface.
    /// </remarks>
    JsValue InlineEventHandler(DomNode node, string eventType);
}
