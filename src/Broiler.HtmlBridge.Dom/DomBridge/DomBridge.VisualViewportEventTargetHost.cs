using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit IVisualViewportEventTargetHost implementation for the VisualViewportEventTargetBinding
// feature module (Phase 3): the bridge exposes the visual-viewport scroll listener store (from the
// P2.5 EventTargetRegistry) via explicit interface members, so the module never reaches an arbitrary
// bridge private field and the public surface is unchanged.
//
// The contract is spelled in JSEAL handles now, so the binding names no engine type. The registry
// behind it is not migrated: Runtime/EventTargetRegistry.cs stores the listeners as engine functions
// and LayoutMetrics.Scrolling.cs invokes them directly, so the handle is unwrapped here — the one
// place that still has to name the engine's function type, and the line that disappears when the
// registry moves to JSEAL. JsInterop is a cast rather than a conversion, so identity is preserved and
// removeEventListener keeps finding the listener addEventListener stored.
public sealed partial class DomBridge : Dom.Features.IVisualViewportEventTargetHost
{
    void Dom.Features.IVisualViewportEventTargetHost.AddVisualViewportScrollListener(JsValue listener)
    {
        if (ToEngineListener(listener) is { } engineListener)
            _eventTargets.AddVisualViewportScrollListener(engineListener);
    }

    void Dom.Features.IVisualViewportEventTargetHost.RemoveVisualViewportScrollListener(JsValue listener)
    {
        if (ToEngineListener(listener) is { } engineListener)
            _eventTargets.RemoveVisualViewportScrollListener(engineListener);
    }

    /// <summary>The engine function behind a listener handle, or <see langword="null"/> if it is not one.</summary>
    private static JavaScript.BuiltIns.Function.JSFunction? ToEngineListener(JsValue listener) =>
        listener.IsFunction ? Dom.Runtime.JsInterop.ToEngineObject(listener) as JavaScript.BuiltIns.Function.JSFunction : null;
}
