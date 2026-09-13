using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit ICanvasHost implementation for the CanvasBinding feature module: the canvas context reaches
// the realm's Uint8ClampedArray constructor through the window object and nothing else, so the module
// touches no bridge private state and the public surface is unchanged.
//
// The window is the bridge's root, a handle, so this forwards it. A bridge that has not registered a
// window yet still answers `undefined` and not the Missing the root holds: the module's `IsObject`
// guard reads the two alike, but `undefined` is what this member has answered since the contract
// stopped expressing the absence as a CLR null, and dropping the coalesce would have compiled without
// a word.
public sealed partial class DomBridge : Dom.Features.ICanvasHost
{
    JsValue Dom.Features.ICanvasHost.Window =>
        WindowHandle.IsMissing ? JsValue.Undefined : WindowHandle;
}
