using Broiler.Dom;
using Broiler.HtmlBridge.Jseal;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="WindowScrollBinding"/> needs from the bridge: the document
/// (scrolling) element, the JS scroll-argument parser (numeric x/y or a <c>ScrollToOptions</c> dict
/// with <c>behavior</c>), and the scroll primitive that applies an absolute/relative offset with a
/// scroll behavior.
/// </summary>
/// <remarks>
/// The contract names no engine type. The argument parser takes the call's own argument span rather
/// than an engine frame, which is what lets the bridge answer it with the one reading it already
/// gives the sub-window contract instead of a second copy — see
/// <c>DomBridge.WindowScrollHost.cs</c>.
/// </remarks>
internal interface IWindowScrollHost
{
    DomElement DocumentElement { get; }

    /// <summary>
    /// <c>scroll(x, y)</c> / <c>scroll({ left, top, behavior })</c>: an options object wins over
    /// positional coordinates, an absent or nullish member is "leave this axis alone", and a blank
    /// behaviour is none. Both coercions are the realm's, because <c>scrollTo("100", "200")</c> is a
    /// page passing strings.
    /// </summary>
    (double? Left, double? Top, string? Behavior) GetScrollArguments(ReadOnlySpan<JsValue> arguments);

    void SetElementScrollOffsetsWithBehavior(
        DomElement element, double? left, double? top, bool relative, bool clamp, string? behavior);
}
