using Broiler.JSeal;
using Broiler.HtmlBridge.Dom.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>addEventListener</c>/<c>removeEventListener</c> registration semantics -- the listener half
/// of the Events feature, alongside the <see cref="EventDispatchBinding"/> dispatch half. This is
/// pure logic over a resolved per-type listener list plus the JS <c>options</c> argument: option
/// parsing (capture/once/passive), the DOM duplicate-registration check, and
/// match-by-listener-and-capture removal, plus snapshot invocation shared by the dispatch paths. It
/// is deliberately stateless and storage-agnostic -- each target callback (element, document,
/// window, message port) resolves its own listener store from the
/// <see cref="EventTargetRegistry"/> and calls these operations, so the registration block is
/// written once rather than per feature file. A caller that holds the whole per-type store hands it
/// over as it is: <see cref="AddTo"/> and <see cref="RemoveFrom"/> do the by-type resolution --
/// including the create-on-first-add -- so the get-or-create is written once too.
/// </summary>
/// <remarks>
/// <para>
/// <b>The four bindings call this directly, with the realm their call frame carries.</b> A
/// registration is an <c>EventListenerRegistration</c>, declared in <c>DomBridge/RuntimeStates.cs</c>,
/// and its listener field is a <see cref="JsValue"/>. No host member converts on the way in.
/// </para>
/// <para>
/// <b>Identity was read rather than assumed.</b> <see cref="JsValue"/>'s <c>==</c> compares kind and
/// then, for every object, function and array, <c>ReferenceEquals</c> over the engine object the
/// handle carries -- one question about one instance. A listener and the handle it is later removed with both come
/// from a call frame the provider filled, so their kinds agree by construction, and
/// <c>removeEventListener</c> finds exactly the registration <c>addEventListener</c> made, including
/// for a <c>handleEvent</c> object.
/// </para>
/// <para>
/// <b>The coercions are the realm's, and one of them could not have been the handle's.</b> An options
/// object's three flags are read with <see cref="IJsMembers.GetProperty"/>, so a getter a page
/// installed runs once per flag, in the order capture, once, passive -- and a non-object
/// <c>options</c> is coerced with <see cref="IJsValues.ToBoolean"/>. Not
/// <see cref="JsValue.AsBoolean"/>: a handle carries a BigInt as an opaque provider reference and
/// answers <see langword="true"/> for <c>0n</c>, where the ECMAScript coercion answers false, and
/// <c>addEventListener(t, f, 0n)</c> must be capture-false. The object test is
/// <see cref="JsValue.IsObject"/>, which admits an object, a function and an array -- the three
/// kinds the provider mints for the engine's object type, and no others.
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
    /// The whole of DOM <c>addEventListener</c> over a target's per-type listener store: the arity
    /// test, the event-type coercion, the create-on-first-add of that type's list, and
    /// <see cref="AddListener"/>. The caller supplies the store and nothing else.
    /// </summary>
    /// <remarks>
    /// A call with fewer than two arguments is a no-op that returns <em>before</em> the type is
    /// coerced, so a <c>toString</c> the page wrote on the first argument does not run for it --
    /// and no list is filed for that type either. An unsupplied third argument is
    /// <see cref="JsValue.Undefined"/>, which the option parsing reads as capture-false.
    /// </remarks>
    internal static JsValue AddTo(
        Dictionary<string, List<EventListenerRegistration>> byType, in JsCall call)
    {
        if (call.Length < 2)
            return JsValue.Undefined;

        var type = call.Realm.ToJsString(call[0]);
        if (!byType.TryGetValue(type, out var listeners))
        {
            listeners = [];
            byType[type] = listeners;
        }

        AddListener(call.Realm, listeners, call[1], call.Length > 2 ? call[2] : JsValue.Undefined);
        return JsValue.Undefined;
    }

    /// <summary>
    /// <see cref="AddTo"/>'s counterpart for DOM <c>removeEventListener</c>, with the same arity
    /// test and coercion in the same order. Nothing is created here: a target with no store at all
    /// (<see langword="null"/>) and a store holding no list of that type both reach
    /// <see cref="RemoveListener"/>'s no-op.
    /// </summary>
    internal static JsValue RemoveFrom(
        Dictionary<string, List<EventListenerRegistration>>? byType, in JsCall call)
    {
        if (call.Length < 2)
            return JsValue.Undefined;

        var type = call.Realm.ToJsString(call[0]);
        RemoveListener(
            call.Realm,
            byType is not null && byType.TryGetValue(type, out var listeners) ? listeners : null,
            call[1], call.Length > 2 ? call[2] : JsValue.Undefined);
        return JsValue.Undefined;
    }

    /// <summary>
    /// Invokes a snapshot of the matching registrations. Removal remains visible to snapshots,
    /// while additions wait for a later invocation of the target's listeners.
    /// </summary>
    /// <param name="capturePhase">
    /// Restricts invocation to registrations whose capture flag matches. When <see langword="null"/>
    /// — the default, and what the document, form-submission and message-port callers pass — every
    /// registration fires regardless of its capture flag.
    /// </param>
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
