namespace Broiler.HtmlBridge;

/// <summary>What the pointer did: a button went down or came up, or the pointer moved, or left the page.</summary>
public enum PointerInputKind
{
    /// <summary>A button was pressed.</summary>
    Down,

    /// <summary>A button was released.</summary>
    Up,

    /// <summary>The pointer moved over the page, with or without a button held (<see cref="PointerInput.Buttons"/>).</summary>
    Move,

    /// <summary>The pointer left the page: the host's view of it, or the host's window.</summary>
    Leave,
}

/// <summary>
/// A user's press or release of a pointer button over a page, or a move of the pointer, as the page's
/// host delivers it to the page's scripts (<c>InteractiveSession.DispatchPointer</c>).
/// </summary>
/// <param name="Kind">What the pointer did.</param>
/// <param name="X">
/// Where, across the page's layout, in CSS pixels: the point in the viewport plus the viewport's
/// scroll. The bridge lays its document out at <c>DomBridge.ViewportWidth</c>, so a host that shows
/// the page at another size sets that first, or the point lands on another element than the one shown.
/// </param>
/// <param name="Y">As <paramref name="X"/>, down the page's layout.</param>
public readonly record struct PointerInput(PointerInputKind Kind, double X, double Y)
{
    /// <summary>How far the viewport is scrolled across, in CSS pixels; <c>clientX</c> is <see cref="X"/> less this.</summary>
    public double ScrollX { get; init; }

    /// <summary>How far the viewport is scrolled down, in CSS pixels; <c>clientY</c> is <see cref="Y"/> less this.</summary>
    public double ScrollY { get; init; }

    /// <summary>The button that changed: 0 the main one, 1 the middle one, 2 the secondary one (<c>MouseEvent.button</c>).</summary>
    public int Button { get; init; }

    /// <summary>
    /// The buttons held once the change is made -- or while the pointer moves -- as
    /// <c>MouseEvent.buttons</c>' bit mask: 1 main, 2 secondary, 4 middle.
    /// </summary>
    public int Buttons { get; init; }

    /// <summary>
    /// Which press of a run of quick presses at one place this is -- 1, or 2 for the second of a
    /// double click -- as <c>MouseEvent.detail</c> reports it. A double click's second release is
    /// followed by <c>dblclick</c>.
    /// </summary>
    public int ClickCount { get; init; } = 1;

    /// <summary>Whether Control was held.</summary>
    public bool CtrlKey { get; init; }

    /// <summary>Whether Shift was held.</summary>
    public bool ShiftKey { get; init; }

    /// <summary>Whether Alt was held.</summary>
    public bool AltKey { get; init; }

    /// <summary>Whether the platform's meta key (Windows, Command) was held.</summary>
    public bool MetaKey { get; init; }
}

/// <summary>What a <see cref="PointerInput"/> did on the page.</summary>
/// <param name="Delivered">Whether the page's scripts were given the input at all.</param>
/// <param name="DefaultPrevented">
/// Whether the page's scripts cancelled what the input does by default -- the press's
/// <c>pointerdown</c> or <c>mousedown</c>, the release's <c>click</c>, or a move's <c>mousemove</c> --
/// so a host performs none of its own default actions for it: no text selection for a press or a drag,
/// no link followed for a click.
/// </param>
public readonly record struct PointerInputResult(bool Delivered, bool DefaultPrevented);
