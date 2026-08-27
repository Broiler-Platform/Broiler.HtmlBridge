namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The narrow host surface <see cref="MatchMediaBinding"/> needs from the bridge: the current
/// viewport dimensions a media query is evaluated against. Read live (per <c>matchMedia</c> call),
/// so the values reflect the viewport at call time, not at registration time.
/// </summary>
internal interface IMatchMediaHost
{
    int ViewportWidth { get; }
    int ViewportHeight { get; }
}
