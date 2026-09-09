using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit ICanvasHost implementation for the CanvasBinding feature module: the canvas context reaches
// the realm's Uint8ClampedArray constructor through the window object and nothing else, so the module
// touches no bridge private state and the public surface is unchanged.
//
// The window field is still the engine's own object (registration has not migrated), so this is the
// engine-typed half of the seam: JsInterop carries the object across without converting it. A bridge
// that has not registered a window yet answers `undefined`, which is what the module's `IsObject` guard
// reads — the previous contract expressed the same absence as a CLR null.
public sealed partial class DomBridge : Dom.Features.ICanvasHost
{
    JsValue Dom.Features.ICanvasHost.Window =>
        _windowJSObject is { } window
            ? Dom.Runtime.JsInterop.FromEngineObject(window)
            : JsValue.Undefined;
}
