using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>window.visualViewport</c> EventTarget methods — <c>addEventListener</c> /
/// <c>removeEventListener</c> — co-located as an HtmlBridge feature module (Phase 3), completing the
/// EventTarget-wiring trilogy alongside <see cref="DocumentEventTargetBinding"/> (P3.32) and
/// <see cref="WindowEventTargetBinding"/> (P3.33). Only the <c>scroll</c> event is supported; a
/// <c>scroll</c> listener is added to / removed from the visual-viewport store through the narrow
/// <see cref="IVisualViewportEventTargetHost"/> contract (any other type is a no-op). Previously the
/// bridge's <c>JsRegistrationAddEventListener146Core</c>/<c>RemoveEventListener147Core</c> in the
/// shared JsFunctionCallbacks/Registration.cs grab-bag.
/// </summary>
internal static class VisualViewportEventTargetBinding
{
    public static JsValue AddEventListener(IVisualViewportEventTargetHost host, in JsCall call)
    {
        if (IsScrollListener(in call))
            host.AddVisualViewportScrollListener(call[1]);

        return JsValue.Undefined;
    }

    public static JsValue RemoveEventListener(IVisualViewportEventTargetHost host, in JsCall call)
    {
        if (IsScrollListener(in call))
            host.RemoveVisualViewportScrollListener(call[1]);

        return JsValue.Undefined;
    }

    /// <summary>
    /// Whether this call names the one event type the visual viewport dispatches and supplies a
    /// callable for it.
    /// </summary>
    /// <remarks>
    /// The type is coerced through the realm before the listener is examined, and in that order,
    /// because that is the order the two tests were written in: <c>ToJsString</c> can run a
    /// <c>toString</c> the page wrote, so a call whose first argument is an object has a visible side
    /// effect that must not start depending on whether the second argument happened to be a function.
    /// </remarks>
    private static bool IsScrollListener(in JsCall call) =>
        call.Length > 1 &&
        call.Realm.ToJsString(call[0]).Equals("scroll", StringComparison.OrdinalIgnoreCase) &&
        call[1].IsFunction;
}
