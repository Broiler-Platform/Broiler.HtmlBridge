using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>addEventListener</c>/<c>removeEventListener</c> registration semantics (HtmlBridge
/// complexity-reduction roadmap Phase 3, P3.4) — the listener half of the Events feature, alongside
/// the P3.3 <see cref="EventDispatchBinding"/> dispatch half. This is pure logic over a resolved
/// per-type listener list plus the JS <c>options</c> argument: option parsing
/// (capture/once/passive), the DOM duplicate-registration check, and match-by-listener-and-capture
/// removal. It is deliberately stateless and storage-agnostic — each target callback (element,
/// document, window, message port) resolves its own listener list from the P2.5
/// <see cref="EventTargetRegistry"/> and calls these operations, which replaces the same
/// registration block that was previously copied across four feature files.
/// </summary>
internal static class EventListenerBinding
{
    /// <summary>
    /// Registers <paramref name="listener"/> in <paramref name="listeners"/> unless an equal
    /// registration (same listener and capture flag) already exists — DOM <c>addEventListener</c>.
    /// The caller has already resolved (and, if needed, created) the per-type list.
    /// </summary>
    internal static void AddListener(List<EventListenerRegistration> listeners, JSValue listener, JSValue options)
    {
        var registration = CreateEventListenerRegistration(listener, options);
        if (!HasMatchingEventListener(listeners, registration))
            listeners.Add(registration);
    }

    /// <summary>
    /// Removes the first registration matching <paramref name="listener"/> and the capture flag from
    /// <paramref name="options"/> — DOM <c>removeEventListener</c>. A null list (no listeners of that
    /// type) is a no-op.
    /// </summary>
    internal static void RemoveListener(List<EventListenerRegistration>? listeners, JSValue listener, JSValue options)
    {
        if (listeners is null)
            return;

        var capture = GetCaptureForRemoval(options);
        for (var i = listeners.Count - 1; i >= 0; i--)
        {
            if (listeners[i].Listener == listener && listeners[i].Capture == capture)
            {
                listeners.RemoveAt(i);
                break;
            }
        }
    }

    private static EventListenerRegistration CreateEventListenerRegistration(JSValue listener, JSValue options)
    {
        if (options is JSObject optionsObject)
        {
            return new EventListenerRegistration(
                listener,
                GetBooleanOption(optionsObject, "capture"),
                GetBooleanOption(optionsObject, "once"),
                GetBooleanOption(optionsObject, "passive"));
        }

        return new EventListenerRegistration(listener, options.BooleanValue);
    }

    private static bool GetCaptureForRemoval(JSValue options)
        => options is JSObject optionsObject ? GetBooleanOption(optionsObject, "capture") : options.BooleanValue;

    private static bool HasMatchingEventListener(
        List<EventListenerRegistration> listeners,
        EventListenerRegistration candidate)
        // Callers pass the registrations already scoped to a single event type,
        // so the DOM duplicate-registration check only needs listener/capture.
        => listeners.Any(existing =>
            existing.Listener == candidate.Listener &&
            existing.Capture == candidate.Capture);

    private static bool GetBooleanOption(JSObject options, string name)
    {
        var value = options[(KeyString)name];
        return value != null && !value.IsNullOrUndefined && value.BooleanValue;
    }
}
