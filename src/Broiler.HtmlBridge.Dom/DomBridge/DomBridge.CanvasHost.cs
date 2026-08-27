using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

// Explicit ICanvasHost implementation for the CanvasBinding feature module: the canvas context reaches
// the realm's Uint8ClampedArray constructor through the window object and nothing else, so the module
// touches no bridge private state and the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.ICanvasHost
{
    JSObject? Dom.Features.ICanvasHost.WindowJSObject => _windowJSObject;
}
