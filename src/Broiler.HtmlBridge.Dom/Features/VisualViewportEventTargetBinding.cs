using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.BuiltIns.Function;

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
    public static JSValue AddEventListener(IVisualViewportEventTargetHost host, in Arguments a)
    {
        if (a.Length > 1 && a[0].ToString().Equals("scroll", StringComparison.OrdinalIgnoreCase) && a[1] is JSFunction listener)
            host.AddVisualViewportScrollListener(listener);

        return JSUndefined.Value;
    }

    public static JSValue RemoveEventListener(IVisualViewportEventTargetHost host, in Arguments a)
    {
        if (a.Length > 1 && a[0].ToString().Equals("scroll", StringComparison.OrdinalIgnoreCase) && a[1] is JSFunction listener)
            host.RemoveVisualViewportScrollListener(listener);

        return JSUndefined.Value;
    }
}
