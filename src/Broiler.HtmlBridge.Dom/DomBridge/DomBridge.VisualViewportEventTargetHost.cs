using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge;

// Explicit IVisualViewportEventTargetHost implementation for the VisualViewportEventTargetBinding
// feature module (Phase 3): the bridge exposes the visual-viewport scroll listener store (from the
// P2.5 EventTargetRegistry) via explicit interface members, so the module never reaches an arbitrary
// bridge private field and the public surface is unchanged.
//
// The contract is spelled in JSEAL handles, and so is the store behind it: Runtime/EventTargetRegistry.cs
// keeps the handle a listener arrived as, and the scroll dispatch in LayoutMetrics.Scrolling.cs invokes
// that handle through the realm, so nothing in the bridge between the page's addEventListener argument
// and that call names an engine type.
//
// This file used to unwrap every listener to the engine's function type, and its comment gave the two
// files either side of it as the reason: the registry "stores the listeners as engine functions", and
// LayoutMetrics.Scrolling.cs "invokes them directly". The first was so only because this file fed the
// registry engine functions. The second was true when it was written and stopped being true a little
// over two hours later, when the dispatcher began converting every listener back into a handle to
// invoke it through the realm — from then on the unwrap here bought that round trip and nothing else.
// The comment also called this "the one place that still has to name the engine's function type",
// while the registry it pointed at spelled that type four times.
//
// The IsFunction test is the half of the old conversion that was a decision rather than a change of
// type, and it stays. VisualViewportEventTargetBinding.IsScrollListener makes the same test first and
// is this contract's only caller, so it cannot fail today; but the dispatcher invokes whatever the
// store holds, and with every element kind Function, JsValue's kind-then-reference equality asks
// List.Contains and List.Remove exactly what they asked of the engine's functions.
public sealed partial class DomBridge : Dom.Features.IVisualViewportEventTargetHost
{
    void Dom.Features.IVisualViewportEventTargetHost.AddVisualViewportScrollListener(JsValue listener)
    {
        if (listener.IsFunction)
            _eventTargets.AddVisualViewportScrollListener(listener);
    }

    void Dom.Features.IVisualViewportEventTargetHost.RemoveVisualViewportScrollListener(JsValue listener)
    {
        if (listener.IsFunction)
            _eventTargets.RemoveVisualViewportScrollListener(listener);
    }
}
