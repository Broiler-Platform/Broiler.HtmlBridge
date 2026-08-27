using Broiler.JavaScript.BuiltIns.Null;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.JavaScript.Runtime;
using Broiler.Dom;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The inline <c>on*</c> event-handler IDL reflectors (<c>onclick</c>, <c>onload</c>, … — one property
/// per <c>InlineEventNames</c> entry), registered on every element wrapper, co-located as an HtmlBridge
/// feature module (Phase 3). The getter returns the stored handler function or <c>null</c>; the setter
/// stores a function or, given a non-function, clears the entry — both against the bridge's live
/// inline-handler map, reached through the <see cref="IEventHandlerReflectorHost"/> contract. Was the
/// bridge's <c>JsJsObjectsCallback104Core</c> (get) / <c>JsJsObjectsCallback105Core</c> (set).
/// </summary>
internal static class EventHandlerReflectorBinding
{
    public static JSValue GetOn(IEventHandlerReflectorHost host, DomElement element, string? eventName, in Arguments _)
    {
        if (host.GetInlineEventHandlers(element).TryGetValue(eventName, out var handler))
            return handler;
        return JSNull.Value;
    }

    public static JSValue SetOn(IEventHandlerReflectorHost host, DomElement element, string? eventName, in Arguments a)
    {
        if (a.Length > 0 && a[0] is JSFunction fn)
            host.GetInlineEventHandlers(element)[eventName] = fn;
        else
            host.GetInlineEventHandlers(element).Remove(eventName);
        return JSUndefined.Value;
    }
}
