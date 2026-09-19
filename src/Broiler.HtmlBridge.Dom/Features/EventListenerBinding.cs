using Broiler.JSeal;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>addEventListener</c>/<c>removeEventListener</c> registration semantics (HtmlBridge
/// complexity-reduction roadmap Phase 3, P3.4) -- the listener half of the Events feature, alongside
/// the P3.3 <see cref="EventDispatchBinding"/> dispatch half. This is pure logic over a resolved
/// per-type listener list plus the JS <c>options</c> argument: option parsing
/// (capture/once/passive), the DOM duplicate-registration check, and match-by-listener-and-capture
/// removal, plus snapshot invocation shared by the dispatch paths. It is deliberately stateless
/// and storage-agnostic -- each target callback (element,
/// document, window, message port) resolves its own listener list from the P2.5
/// <see cref="EventTargetRegistry"/> and calls these operations, which replaces the same
/// registration block that was previously copied across four feature files.
/// </summary>
/// <remarks>
/// <para>
/// <b>The four bindings call this directly, with the realm their call frame carries.</b> A
/// registration is an <c>EventListenerRegistration</c>, declared in <c>DomBridge/RuntimeStates.cs</c>,
/// and its listener field is a <see cref="JsValue"/>. This module used to be engine-typed on that
/// field's account, and every binding reached it through a host member that first converted a handle
/// into an engine value -- one converter shared by the element, document and window contracts, with
/// the messaging contract forwarding to the element one. The record, this module and those members
/// moved together, because narrowing any one alone would only have moved the conversion; the members
/// are deleted rather than left forwarding, which is what the document contract's own remark said
/// would happen when the record moved.
/// </para>
/// <para>
/// <b>Identity is unchanged, and it was read rather than assumed.</b> The comparisons below were C#
/// <c>==</c> on the engine's value type, which declares no <c>operator ==</c>, so they were CLR
/// reference equality. <see cref="JsValue"/>'s <c>==</c> compares kind and then, for every object,
/// function and array, <c>ReferenceEquals</c> over the engine object the handle carries -- the same
/// question about the same instance. A listener and the handle it is later removed with both come
/// from a call frame the provider filled, so their kinds agree by construction, and
/// <c>removeEventListener</c> finds exactly the registration <c>addEventListener</c> made, including
/// for a <c>handleEvent</c> object.
/// </para>
/// <para>
/// <b>The coercions are the realm's, and one of them could not have been the handle's.</b> An options
/// object's three flags are read with <see cref="IJsMembers.GetProperty"/> -- the same indexer over
/// the same object, so a getter a page installed runs once per flag, in the order capture, once,
/// passive, exactly as before -- and a non-object <c>options</c> is coerced with
/// <see cref="IJsValues.ToBoolean"/>. Not <see cref="JsValue.AsBoolean"/>: a handle carries a BigInt as
/// an opaque provider reference and answers <see langword="true"/> for <c>0n</c>, where the engine's
/// own coercion answered false. <c>addEventListener(t, f, 0n)</c> is capture-false before this module
/// moved and after it. The object test is <see cref="JsValue.IsObject"/>, which admits an object, a
/// function and an array -- exactly what the engine-typed pattern admitted, because the provider mints
/// those three kinds for the engine's object type and for nothing else.
/// </para>
/// </remarks>
internal static class EventListenerBinding
{
    /// <summary>
    /// Registers <paramref name="listener"/> in <paramref name="listeners"/> unless an equal
    /// registration (same listener and capture flag) already exists -- DOM <c>addEventListener</c>.
    /// The caller has already resolved (and, if needed, created) the per-type list.
    /// </summary>
    internal static void AddListener(
        IJsRealm realm, List<EventListenerRegistration> listeners, JsValue listener, JsValue options)
    {
        var registration = CreateEventListenerRegistration(realm, listener, options);
        if (!HasMatchingEventListener(listeners, registration))
            listeners.Add(registration);
    }

    /// <summary>
    /// Removes the first registration matching <paramref name="listener"/> and the capture flag from
    /// <paramref name="options"/> -- DOM <c>removeEventListener</c>. A null list (no listeners of that
    /// type) is a no-op, and returns before <paramref name="options"/> is read.
    /// </summary>
    internal static void RemoveListener(
        IJsRealm realm, List<EventListenerRegistration>? listeners, JsValue listener, JsValue options)
    {
        if (listeners is null)
            return;

        var capture = GetCaptureForRemoval(realm, options);
        for (var i = listeners.Count - 1; i >= 0; i--)
        {
            if (listeners[i].Listener == listener && listeners[i].Capture == capture)
            {
                listeners[i].Removed = true;
                listeners.RemoveAt(i);
                break;
            }
        }
    }

    /// <summary>
    /// Invokes a snapshot of the matching registrations. Removal remains visible to snapshots,
    /// while additions wait for a later invocation of the target's listeners.
    /// </summary>
    internal static void InvokeListeners(
        List<EventListenerRegistration> listeners, Action<JsValue> invoke,
        ref bool immediateStopped, ref bool currentListenerPassive, bool? capturePhase = null)
    {
        foreach (var registration in listeners.ToArray())
        {
            if (immediateStopped)
                break;
            if (registration.Removed ||
                (capturePhase.HasValue && registration.Capture != capturePhase.Value))
                continue;

            // Remove before calling page code: a nested dispatch must not see this registration,
            // and the callback may register itself again without reviving the old snapshot entry.
            if (registration.Once)
            {
                registration.Removed = true;
                listeners.Remove(registration);
            }

            currentListenerPassive = registration.Passive;
            try { invoke(registration.Listener); }
            finally { currentListenerPassive = false; }
        }
    }

    private static EventListenerRegistration CreateEventListenerRegistration(
        IJsRealm realm, JsValue listener, JsValue options)
    {
        if (options.IsObject)
        {
            return new EventListenerRegistration(
                listener,
                GetBooleanOption(realm, options, "capture"),
                GetBooleanOption(realm, options, "once"),
                GetBooleanOption(realm, options, "passive"));
        }

        return new EventListenerRegistration(listener, realm.ToBoolean(options));
    }

    private static bool GetCaptureForRemoval(IJsRealm realm, JsValue options)
        => options.IsObject ? GetBooleanOption(realm, options, "capture") : realm.ToBoolean(options);

    private static bool HasMatchingEventListener(
        List<EventListenerRegistration> listeners,
        EventListenerRegistration candidate)
        // Callers pass the registrations already scoped to a single event type,
        // so the DOM duplicate-registration check only needs listener/capture.
        => listeners.Any(existing =>
            existing.Listener == candidate.Listener &&
            existing.Capture == candidate.Capture);

    /// <remarks>
    /// A property that was never installed reads back as <see cref="JsValue.Missing"/> where the engine
    /// indexer answered a CLR <see langword="null"/>, so <see cref="JsValue.IsNullish"/> is exactly the
    /// three cases the engine-typed test named: absent, <c>null</c> and <c>undefined</c>.
    /// </remarks>
    private static bool GetBooleanOption(IJsRealm realm, JsValue options, string name)
    {
        var value = realm.GetProperty(options, name);
        return !value.IsNullish && realm.ToBoolean(value);
    }
}
