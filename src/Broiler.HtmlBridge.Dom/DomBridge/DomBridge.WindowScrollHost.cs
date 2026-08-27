using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge;

// Explicit IWindowScrollHost implementation for the WindowScrollBinding feature module (Phase 3):
// the bridge exposes the document (scrolling) element, the JS scroll-argument parser and the scroll
// primitive via explicit interface members, so the module never reaches an arbitrary bridge private
// field and the public surface is unchanged.
public sealed partial class DomBridge : Dom.Features.IWindowScrollHost
{
    DomElement Dom.Features.IWindowScrollHost.DocumentElement => DocumentElement;

    (double? Left, double? Top, string? Behavior) Dom.Features.IWindowScrollHost.GetScrollArguments(in Arguments args)
        => GetScrollArguments(in args);

    void Dom.Features.IWindowScrollHost.SetElementScrollOffsetsWithBehavior(
        DomElement element, double? left, double? top, bool relative, bool clamp, string? behavior)
        => SetElementScrollOffsetsWithBehavior(element, left, top, relative, clamp, behavior);
}
