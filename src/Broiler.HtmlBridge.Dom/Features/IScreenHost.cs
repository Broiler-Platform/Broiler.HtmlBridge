namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="ScreenBinding"/> needs from the bridge:
/// the screen geometry reported by <c>window.screen</c>.
/// </summary>
internal interface IScreenHost
{
    int ScreenWidth { get; }
    int ScreenHeight { get; }
    int ScreenAvailWidth => ScreenWidth;
    int ScreenAvailHeight => ScreenHeight;
    int ScreenAvailLeft => 0;
    int ScreenAvailTop => 0;
    int ScreenColorDepth => 24;
    int ScreenPixelDepth => 24;
}
