using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit IWindowScrollHost implementation for the WindowScrollBinding feature module (Phase 3):
// the bridge exposes the document (scrolling) element, the JS scroll-argument parser and the scroll
// primitive via explicit interface members, so the module never reaches an arbitrary bridge private
// field and the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IWindowScrollHost
{
    DomElement Dom.Features.IWindowScrollHost.DocumentElement => DocumentElement;

    // One reading, not two: the JSEAL-framed argument list is read by the bridge's own ISubWindowHost
    // member, which performs exactly what the engine-framed GetScrollArguments this replaces
    // performed — an options object wins over positional coordinates, a nullish member leaves its
    // axis alone, and both coercions are the realm's, as DoubleValue and ToString() were the
    // engine's. Forwarding is how the window and sub-window contracts share that one reading rather
    // than each carrying a copy of it.
    (double? Left, double? Top, string? Behavior) Dom.Features.IWindowScrollHost.GetScrollArguments(
        ReadOnlySpan<JsValue> arguments)
        => ((Dom.Features.ISubWindowHost)this).GetScrollArguments(arguments);

    void Dom.Features.IWindowScrollHost.SetElementScrollOffsetsWithBehavior(
        DomElement element, double? left, double? top, bool relative, bool clamp, string? behavior)
        => SetElementScrollOffsetsWithBehavior(element, left, top, relative, clamp, behavior);
}
