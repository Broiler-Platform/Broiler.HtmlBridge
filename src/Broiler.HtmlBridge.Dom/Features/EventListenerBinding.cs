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
/// <remarks>
/// <para>
/// <b>This module is engine-typed on purpose, and the pin is a one-line declaration it does not
/// own.</b> A registration is an <c>EventListenerRegistration</c>, declared in
/// <c>DomBridge/RuntimeStates.cs</c>, whose listener field is a Broiler.JS value. Everything below
/// follows from that field: it is what the two operations put into the list and what they compare
/// when removing, so the record's type is this module's type. Changing it takes three files, and the
/// events slice does not contain them — the record itself, plus the two host implementations that
/// hand these operations engine values today (<c>DomBridge/DomBridge.DocumentEventTargetHost.cs</c>
/// and <c>DomBridge/DomBridge.WindowEventTargetHost.cs</c>, whose shared <c>ToEngineListenerValue</c>
/// exists for no other reason and dies with it). Migrating the option arguments alone would only move
/// the conversion, so the whole registration surface stays as it is until those three move together.
/// </para>
/// <para>
/// <b>Identity survives that move when it comes — read, not assumed.</b> The comparisons below are
/// C# <c>==</c> on the engine's value type, which declares no <c>operator ==</c>, so today they are
/// CLR reference equality. <c>JsValue.operator ==</c> implements ECMAScript strict equality by kind,
/// and its default arm — every object, function and array kind — is
/// <c>ReferenceEquals(left.Reference, right.Reference)</c> over the engine object the handle carries,
/// which is the same question. So <c>removeEventListener</c> would keep finding exactly the
/// registration <c>addEventListener</c> made, including for a <c>handleEvent</c> object.
/// <c>List.Any</c> and <c>List.Remove</c> reach <c>Equals</c>/<c>GetHashCode</c> rather than the
/// operator: <c>JsValue.Equals</c> takes the same reference arm, and <c>GetHashCode</c> answers
/// <c>RuntimeHelpers.GetHashCode(Reference)</c> for it, so both are reflexive for an object and a
/// registration can be found in the list it was put into. The one documented divergence between the
/// two is NaN, which no listener can be. <c>Runtime/EventTargetRegistry.cs</c> has since re-keyed its
/// two object maps on that pair, so the reading is a shipped one rather than a prediction.
/// </para>
/// <para>
/// <b>And the move fixes something rather than merely relocating it.</b> A handle carries a primitive
/// by value where the engine's value type carries an instance, so the re-materialisation the host
/// implementations perform today — a fresh engine string or boolean per call — makes
/// <c>removeEventListener(t, "handler")</c> unable to find what <c>addEventListener(t, "handler")</c>
/// registered. WebIDL does not admit that listener shape at all and nothing in the bridge dispatches
/// one, which is why it is a reported quirk rather than a live bug; <c>JsValue</c> compares a string
/// and a boolean by value, so it simply stops being possible.
/// </para>
/// </remarks>
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
