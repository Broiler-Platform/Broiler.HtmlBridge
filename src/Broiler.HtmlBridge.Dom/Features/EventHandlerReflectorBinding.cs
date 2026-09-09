using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The inline <c>on*</c> event-handler IDL reflectors (<c>onclick</c>, <c>onload</c>, … — one property
/// per <c>InlineEventNames</c> entry), registered on every element wrapper, co-located as an HtmlBridge
/// feature module (Phase 3). The getter returns the stored handler function or <c>null</c>; the setter
/// stores a function or, given a non-function, clears the entry — both against the bridge's live
/// inline-handler store, reached through the <see cref="IEventHandlerReflectorHost"/> contract. Was the
/// bridge's <c>JsJsObjectsCallback104Core</c> (get) / <c>JsJsObjectsCallback105Core</c> (set).
/// </summary>
internal static class EventHandlerReflectorBinding
{
    /// <summary>
    /// The reflector's getter. It reads no argument — an IDL attribute getter is called with none —
    /// and the frame is taken only so the pair reads as a pair at the call site.
    /// </summary>
    public static JsValue GetOn(IEventHandlerReflectorHost host, DomElement element, string eventName, in JsCall _) =>
        host.GetInlineEventHandler(element, eventName);

    /// <summary>
    /// The reflector's setter: a function is installed, and anything else — <c>null</c>,
    /// <c>undefined</c>, a string, or no argument at all — clears the entry.
    /// </summary>
    /// <remarks>
    /// <c>IsFunction</c> is the same callability question the engine type test here used to ask, and
    /// it is decided from the handle without entering the engine — the provider answered it once, when
    /// it filled the call frame, which is why the kind is on the handle at all. An absent argument is
    /// <see cref="JsValue.Missing"/>, which is not a function, so the arity guard the old code spelled
    /// as <c>a.Length &gt; 0</c> is already in the same question.
    /// </remarks>
    public static JsValue SetOn(IEventHandlerReflectorHost host, DomElement element, string eventName, in JsCall call)
    {
        if (call[0].IsFunction)
            host.SetInlineEventHandler(element, eventName, call[0]);
        else
            host.RemoveInlineEventHandler(element, eventName);

        return JsValue.Undefined;
    }
}
