using Broiler.HtmlBridge.Jseal;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow bridge services the <see cref="EventDispatchBinding"/> feature module needs
/// (HtmlBridge complexity-reduction roadmap Phase 3). The capture → target → bubble dispatch
/// algorithm needs the realm the event object lives in, JS-wrapper identity, the document/window
/// global wrappers (the event path's endpoints), and read access to the per-node listener store
/// (P2.5 <c>EventTargetRegistry</c>) and the inline <c>on*</c> handler for one event type — nothing
/// else. Listener registration and inline-handler compilation stay in the bridge; this module only
/// reads what it dispatches.
/// </summary>
/// <remarks>
/// <para>
/// The contract names no engine type: wrappers are <see cref="JsValue"/> handles and the realm is
/// where the event object's members are installed. This used to say two things behind it did
/// <em>not</em> move. One of them has since, and both are recorded here rather than hidden: the one
/// that has not, and why the one that has was never waiting on what it was said to wait on.
/// </para>
/// <para>
/// <b><see cref="GetEventListeners"/> hands back engine-typed registrations.</b>
/// <c>EventListenerRegistration</c> stores its listener as an engine value and lives in
/// <c>DomBridge/RuntimeStates.cs</c>, which this migration round does not own; it is shared with the
/// window, form-submit and messaging dispatch paths, so its shape cannot move for the events slice
/// alone. The listener is invoked through <c>DomBridge.InvokeEventListener</c>, which is likewise
/// still engine-typed because unmigrated callers share it.
/// </para>
/// <para>
/// <b><see cref="InlineEventHandler"/> is narrower than the map it reads, and no longer because of the
/// map's type.</b> This paragraph said the inline <c>on*</c> handlers lived on
/// <c>InlineStyleRuntimeState</c> as a dictionary of engine values in "the same unowned file", so the
/// bridge did the engine-typed lookup on its own side. The map is a dictionary of
/// <see cref="JsValue"/> handles now, and the file was never the constraint: every reader and writer
/// the map has is in three bridge files, which moved together. The contract still asks for the one
/// handler it fires rather than for the map, because the map is also the reflector's and the attribute
/// compiler's store, and a dispatch module has no business writing to it.
/// </para>
/// </remarks>
internal interface IEventDispatchHost
{
    /// <summary>
    /// The realm the event object's members are installed in and its listeners are called through.
    /// </summary>
    IJsRealm Realm { get; }

    /// <summary>Returns the single JS wrapper identity for <paramref name="node"/>.</summary>
    JsValue WrapNode(DomNode node);

    /// <summary>The main document root node (the top of the event propagation path).</summary>
    DomNode DocumentNode { get; }

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
    /// callable, which is the callability test the dispatch loop used to make itself. The test stays on
    /// the bridge's side because the store is the bridge's, not because it is engine-typed, which is what
    /// this used to say; it is a read of the stored handle's kind. See the remarks on this interface.
    /// </remarks>
    JsValue InlineEventHandler(DomNode node, string eventType);
}
