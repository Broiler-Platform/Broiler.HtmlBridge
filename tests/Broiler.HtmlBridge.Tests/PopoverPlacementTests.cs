using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// What the bridge's top-layer pass (<see cref="DomBridge.ResolveAnchorPositions"/>, which a host that renders a
/// page runs) writes for an open popover: only what the renderer cannot know. HTML's own rule,
/// <c>[popover] { position: fixed; inset: 0; margin: auto; width/height: fit-content }</c>, is the renderer's, so
/// the popover is centred and an author's insets combine with it as in Chromium (measured).
/// </summary>
/// <remarks>
/// The pass wrote <c>top: 0; left: 0</c> into every popover with no position of its own, which put it at the
/// viewport's top-left corner whatever the rule said.
/// </remarks>
public class PopoverPlacementTests
{
    private const string PageUrl = "https://example.test/popover-placement";

    /// <summary>
    /// A popover keeps the UA's <c>fixed</c> and gets no insets; one an author made <c>static</c> is
    /// <c>absolute</c>, as a box in the top layer computes; one placed by <c>position-area</c> has its auto
    /// margins 0, as Chromium uses them there, but keeps the one its author gave.
    /// </summary>
    [Fact]
    public void ThePassLeavesAPopoversPlaceToTheRenderer()
    {
        var factory = new CapturingBridgeFactory();
        using var session = new ScriptEngine(factory).ExecuteInteractive(
            ["['p', 's', 'a'].forEach(function (id) { document.getElementById(id).showPopover(); });"], [],
            "<html><body><style>#s { position: static } #btn { anchor-name: --b }" +
            "#a { position-anchor: --b; position-area: bottom; margin-left: 5px }</style>" +
            "<button id=\"btn\">b</button><div id=\"p\" popover=\"manual\">p</div><div id=\"s\" popover=\"manual\">s</div>" +
            "<div id=\"a\" popover=\"manual\">a</div></body></html>",
            PageUrl);
        Assert.NotNull(session);
        Assert.NotNull(factory.Bridge);

        factory.Bridge!.ResolveAnchorPositions(1024, 768);
        var html = session!.CurrentHtml();

        var plain = Style(html, "p");
        Assert.Contains("position: fixed", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("top:", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("left:", plain, StringComparison.Ordinal);

        Assert.Contains("position: absolute", Style(html, "s"), StringComparison.Ordinal);

        var area = Style(html, "a");
        Assert.Contains("margin-top: 0", area, StringComparison.Ordinal);
        Assert.Contains("margin-right: 0", area, StringComparison.Ordinal);
        Assert.Contains("margin-bottom: 0", area, StringComparison.Ordinal);
        Assert.DoesNotContain("margin-left: 0", area, StringComparison.Ordinal);
    }

    /// <summary>The serialized <c>style</c> attribute of the element with <paramref name="id"/>, or empty.</summary>
    private static string Style(string html, string id)
    {
        var at = html.IndexOf($"id=\"{id}\"", StringComparison.Ordinal);
        Assert.True(at >= 0, $"No element {id} in: {html}");
        var open = html.LastIndexOf('<', at);
        var close = html.IndexOf('>', at);
        var tag = html[open..close];
        var style = tag.IndexOf("style=\"", StringComparison.Ordinal);
        return style < 0 ? string.Empty : tag[(style + 7)..tag.IndexOf('"', style + 7)];
    }

    /// <summary>Hands the engine an ordinary bridge and keeps hold of it.</summary>
    private sealed class CapturingBridgeFactory : Dom.IDomBridgeRuntimeFactory
    {
        internal DomBridge? Bridge { get; private set; }

        public Dom.IDomBridgeRuntime Create() => Bridge = new DomBridge();
    }
}
