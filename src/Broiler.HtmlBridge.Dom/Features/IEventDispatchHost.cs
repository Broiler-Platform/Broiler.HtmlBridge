using Broiler.JavaScript.Runtime;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow bridge services the <see cref="EventDispatchBinding"/> feature module needs
/// (HtmlBridge complexity-reduction roadmap Phase 3). The capture → target → bubble dispatch
/// algorithm needs JS-wrapper identity, the document/window global wrappers (the event path's
/// endpoints), and read access to the per-node listener store (P2.5 <c>EventTargetRegistry</c>) and
/// inline <c>on*</c> handler map — nothing else. Listener registration and inline-handler
/// compilation stay in the bridge; this module only reads what it dispatches.
/// </summary>
internal interface IEventDispatchHost
{
    /// <summary>Returns the single JS wrapper identity for <paramref name="node"/>.</summary>
    JSObject ToJSObject(DomNode node);

    /// <summary>The main document root node (the top of the event propagation path).</summary>
    DomNode DocumentNode { get; }

    /// <summary>The JS <c>document</c> wrapper, used as the event target/currentTarget when the
    /// document node is on the path; null before the document global is installed.</summary>
    JSObject? DocumentJSObject { get; }

    /// <summary>The JS <c>window</c> wrapper appended to <c>composedPath()</c>; null before the
    /// window global is installed.</summary>
    JSObject? WindowJSObject { get; }

    /// <summary>The per-event-type registered listeners for <paramref name="node"/> (live store).</summary>
    Dictionary<string, List<EventListenerRegistration>> GetEventListeners(DomNode node);

    /// <summary>The inline <c>on*</c> handlers for <paramref name="node"/> (live map).</summary>
    Dictionary<string, JSValue> GetInlineEventHandlers(DomNode node);
}
