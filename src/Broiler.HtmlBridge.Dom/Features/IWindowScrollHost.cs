using Broiler.Dom;
using Broiler.JavaScript.Runtime;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="WindowScrollBinding"/> needs from the bridge: the document
/// (scrolling) element, the JS scroll-argument parser (numeric x/y or a <c>ScrollToOptions</c> dict
/// with <c>behavior</c>), and the scroll primitive that applies an absolute/relative offset with a
/// scroll behavior.
/// </summary>
internal interface IWindowScrollHost
{
    DomElement DocumentElement { get; }

    (double? Left, double? Top, string? Behavior) GetScrollArguments(in Arguments args);

    void SetElementScrollOffsetsWithBehavior(
        DomElement element, double? left, double? top, bool relative, bool clamp, string? behavior);
}
