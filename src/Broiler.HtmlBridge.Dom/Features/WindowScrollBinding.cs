using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The <c>window</c> scroll methods — <c>window.scroll</c>, <c>window.scrollTo</c>,
/// <c>window.scrollBy</c> — co-located as an HtmlBridge feature module (Phase 3). Each parses the JS
/// scroll arguments and applies the offset to the document (scrolling) element with the requested
/// scroll behavior; <c>scroll</c> and <c>scrollTo</c> are absolute (identical), <c>scrollBy</c> is
/// relative. The scrolling element, argument parser and scroll primitive are reached through the
/// narrow <see cref="IWindowScrollHost"/> contract. Previously the bridge's
/// <c>JsRegistrationScroll133Core</c>/<c>ScrollTo134Core</c>/<c>ScrollBy135Core</c> in the shared
/// JsFunctionCallbacks/Registration.cs grab-bag.
/// </summary>
internal static class WindowScrollBinding
{
    // window.scroll(x, y) is a historical alias of window.scrollTo(x, y).
    public static JSValue Scroll(IWindowScrollHost host, in Arguments a) => ScrollTo(host, in a);

    public static JSValue ScrollTo(IWindowScrollHost host, in Arguments a)
    {
        var (left, top, behavior) = host.GetScrollArguments(in a);
        // CSSOM View §"scroll an element": the requested position is normalized to the scrolling
        // box's scrolling area, so a scroll past either end comes to rest at the end rather than
        // off the document. `clamp: false` here let `scrollBy({top: scrollHeight})` — the standard
        // "scroll to the bottom" idiom, since scrollHeight >= the maximum offset — land beyond the
        // content and paint the bare canvas. WPT
        // css/css-view-transitions/reset-state-after-scrolled-view-transition (issue #1538
        // problem 28) is that and nothing else: its own reference performs the same scroll, so
        // both rendered identically here and both disagreed with Chromium.
        host.SetElementScrollOffsetsWithBehavior(host.DocumentElement, left, top, relative: false, clamp: true, behavior: behavior);
        return JSUndefined.Value;
    }

    public static JSValue ScrollBy(IWindowScrollHost host, in Arguments a)
    {
        var (left, top, behavior) = host.GetScrollArguments(in a);
        host.SetElementScrollOffsetsWithBehavior(host.DocumentElement, left, top, relative: true, clamp: true, behavior: behavior);
        return JSUndefined.Value;
    }
}
