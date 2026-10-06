using System.Drawing;
using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// What the page a host renders carries for the top layer and anchor positioning, with no host call of its
/// own: an open modal dialog and a showing popover marked as top-layer boxes in the order they went there, a
/// dialog's <c>::backdrop</c> the UA's <c>rgba(0, 0, 0, 0.1)</c> or the author's, and a box placed by
/// <c>anchor()</c> or <c>position-area</c> where its anchor puts it -- while the live document, which the
/// page's script reads, carries none of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The window never ran the pass that does this.</b> <c>DomBridge.ResolveAnchorPositions</c> was the WPT
/// runner's, baking into the live tree once before a screenshot; a window renders a page that goes on running.
/// So in the window a modal dialog had no backdrop and was painted in its place in the page, under a high
/// <c>z-index</c> and inside an ancestor's clip, and an anchored popover sat where a popover with no anchor
/// does. The pass now runs on the render projection (DomBridge/AnchorResolver/AnchorResolver.cs).
/// </para>
/// <para>Chromium, measured: the scrim is <c>rgba(0, 0, 0, 0.1)</c>, a
/// popover's backdrop transparent; a popover with <c>position-area: bottom</c> sits under its anchor.</para>
/// </remarks>
public class TopLayerRenderTests
{
    private const string PageUrl = "https://example.test/top-layer";

    private static InteractiveSession Start(string script, string body, IReadOnlyDictionary<string, RectangleF>? boxes = null)
    {
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions
        {
            LayoutViewFactory = boxes is null ? null : () => new DeclaredBoxLayoutView(boxes),
        }));
        var session = engine.ExecuteInteractive(
            ["var out = document.getElementById('out');" + script], [],
            $"<html><body>{body}<div id=\"out\"></div></body></html>", PageUrl);
        Assert.NotNull(session);
        return session!;
    }

    [Fact]
    public void AModalDialogIsATopLayerBoxOverTheUasScrim()
    {
        using var session = Start(
            "var d = document.getElementById('d'); d.showModal();",
            "<p>page</p><dialog id=\"d\">modal</dialog>");

        var html = session.CurrentHtml();
        var dialog = Tag(html, "d");

        Assert.Equal("1", Attribute(dialog, "data-broiler-top-layer"));
        Assert.Equal("rgba(0, 0, 0, 0.1)", Attribute(dialog, "data-broiler-backdrop"));
        Assert.Contains("position: fixed", Attribute(dialog, "style") ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void APopoverOpenedOverADialogIsAboveIt()
    {
        using var session = Start(
            "document.getElementById('d').showModal(); document.getElementById('p').showPopover();",
            "<dialog id=\"d\">modal</dialog><div id=\"p\" popover=\"manual\">popover</div>");

        var html = session.CurrentHtml();

        Assert.Equal("1", Attribute(Tag(html, "d"), "data-broiler-top-layer"));
        Assert.Equal("2", Attribute(Tag(html, "p"), "data-broiler-top-layer"));
        Assert.Equal("transparent", Attribute(Tag(html, "p"), "data-broiler-backdrop"));
    }

    [Fact]
    public void AnAuthorsBackdropIsTheBackdrop()
    {
        using var session = Start(
            "document.getElementById('d').showModal();",
            "<style>dialog::backdrop { background-color: rgb(0, 0, 255) }</style><dialog id=\"d\">modal</dialog>");

        Assert.Equal("rgb(0, 0, 255)", Attribute(Tag(session.CurrentHtml(), "d"), "data-broiler-backdrop"));
    }

    /// <summary>
    /// The page's own document is left as it was: no marker, no baked style, and the next render, after the
    /// dialog closes, carries nothing either.
    /// </summary>
    [Fact]
    public void ThePageItselfIsUntouched()
    {
        using var session = Start(
            "var d = document.getElementById('d'); d.showModal();",
            "<dialog id=\"d\">modal</dialog>");

        Assert.Contains("data-broiler-top-layer", session.CurrentHtml(), StringComparison.Ordinal);

        session.RunJavaScriptUrl(
            "javascript:void (out.textContent = [String(d.getAttribute('data-broiler-top-layer')), String(d.getAttribute('style')), " +
            "d.outerHTML.indexOf('broiler') < 0].join(' '), d.close())");
        var html = session.SettleLoadWindow();

        Assert.Equal("null null true", PageProbe.OutOf(html, decode: true));
        Assert.Null(Attribute(Tag(html, "d"), "data-broiler-top-layer"));
        Assert.Null(Attribute(Tag(html, "d"), "data-broiler-backdrop"));
    }

    /// <summary>
    /// A box placed by <c>anchor()</c> is baked against its anchor -- here a declared 80x30 box at (100, 100) --
    /// and a popover with <c>position-area</c> and a named anchor is left, its CSS intact, to the renderer, which
    /// places it (Broiler.HTML's <c>PlacesAnchoredBoxes</c>; Broiler.Layout's anchor placement).
    /// </summary>
    [Fact]
    public void AnAnchoredBoxIsPlacedByItsAnchor()
    {
        using var session = Start(
            "document.getElementById('p').showPopover();",
            "<style>#anc { position: absolute; left: 100px; top: 100px; width: 80px; height: 30px; anchor-name: --a }" +
            "#q { position: fixed; top: anchor(--a bottom); left: anchor(--a left) }" +
            "#p { position-anchor: --a; position-area: bottom; margin: 0 }</style>" +
            "<div id=\"anc\">anchor</div><div id=\"q\">q</div><div id=\"p\" popover=\"manual\">p</div>",
            new Dictionary<string, RectangleF>
            {
                ["anc"] = new(100, 100, 80, 30),
                ["q"] = new(0, 0, 20, 10),
                ["p"] = new(0, 0, 40, 20),
            });

        var html = session.CurrentHtml();

        var q = Attribute(Tag(html, "q"), "style") ?? string.Empty;
        Assert.True(q.Contains("top: 130px", StringComparison.Ordinal), html);
        Assert.Contains("left: 100px", q, StringComparison.Ordinal);

        var p = Attribute(Tag(html, "p"), "style") ?? string.Empty;
        Assert.False(p.Contains("top:", StringComparison.Ordinal), html);
        Assert.False(p.Contains("position-area", StringComparison.Ordinal), html);
    }

    /// <summary>
    /// A popover whose style names no anchor is anchored to what showed it -- the <c>popovertarget</c> button
    /// clicked, or <c>showPopover()</c>'s <c>source</c> -- and to nothing when a script showed it with no source
    /// (Chromium: under the button, and at (0, 0) with no source). The engine knows no invokers, so the drawn
    /// page names the anchor: the button's copy gets an <c>anchor-name</c> and the popover's the same
    /// <c>position-anchor</c>, which the renderer's anchor placement then follows.
    /// </summary>
    [Theory]
    [InlineData("document.getElementById('inv').click()", true)]
    [InlineData("document.getElementById('pi').showPopover({ source: document.getElementById('inv') })", true)]
    [InlineData("document.getElementById('pi').togglePopover({ source: document.getElementById('inv') })", true)]
    [InlineData("document.getElementById('pi').showPopover()", false)]
    public void APopoverIsAnchoredToWhatShowedIt(string show, bool anchored)
    {
        using var session = Start(
            show + ";",
            "<style>#pi { position-area: bottom; margin: 0 }</style>" +
            "<button id=\"inv\" popovertarget=\"pi\">menu</button><div id=\"pi\" popover>items</div>",
            new Dictionary<string, RectangleF>
            {
                ["inv"] = new(100, 100, 80, 30),
                ["pi"] = new(0, 0, 40, 20),
            });

        var html = session.CurrentHtml();
        var style = Attribute(Tag(html, "pi"), "style") ?? string.Empty;
        var invoker = Attribute(Tag(html, "inv"), "style") ?? string.Empty;

        Assert.Equal("popover-open", Attribute(Tag(html, "pi"), "data-broiler-state"));
        Assert.True(style.Contains("position-anchor: --broiler-implicit-anchor-1", StringComparison.Ordinal) == anchored, html);
        Assert.True(invoker.Contains("anchor-name: --broiler-implicit-anchor-1", StringComparison.Ordinal) == anchored, html);
    }

    /// <summary>
    /// An invoker that has an anchor name of its own lends it: the popover anchors to it by that name, and the
    /// invoker keeps it -- other boxes may anchor to it by it. A popover that names its own anchor keeps that one.
    /// </summary>
    [Fact]
    public void AnImplicitAnchorUsesTheInvokersOwnName()
    {
        using var session = Start(
            "document.getElementById('inv').click(); document.getElementById('own').showPopover({ source: document.getElementById('inv') });",
            "<style>#inv { anchor-name: --menu-button } .p { position-area: bottom; margin: 0 } #own { position-anchor: --elsewhere }</style>" +
            "<button id=\"inv\" popovertarget=\"pi\">menu</button><div id=\"pi\" class=\"p\" popover>items</div>" +
            "<div id=\"own\" class=\"p\" popover=\"manual\">own</div><div id=\"there\" style=\"anchor-name: --elsewhere\">x</div>",
            new Dictionary<string, RectangleF> { ["inv"] = new(100, 100, 80, 30) });

        var html = session.CurrentHtml();

        Assert.Contains("position-anchor: --menu-button", Attribute(Tag(html, "pi"), "style") ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain("broiler-implicit", html, StringComparison.Ordinal);
        Assert.DoesNotContain("position-anchor", Attribute(Tag(html, "own"), "style") ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>A modal dialog in a frame is a top-layer box of the frame's, over the scrim, in the markup the frame is rendered from.</summary>
    [Fact]
    public void AFramesModalDialogIsInTheFramesTopLayer()
    {
        using var session = Start(
            string.Empty,
            "<iframe id=\"f\" srcdoc=\"<dialog id=fd>in frame</dialog><script>document.getElementById('fd').showModal()</script>\"></iframe>");

        var srcdoc = Attribute(Tag(session.CurrentHtml(), "f"), "srcdoc") ?? string.Empty;
        var dialog = Tag(srcdoc, "fd");

        Assert.NotNull(Attribute(dialog, "data-broiler-top-layer"));
        Assert.Equal("rgba(0, 0, 0, 0.1)", Attribute(dialog, "data-broiler-backdrop"));
    }

    /// <summary>Control, which passes before and after: a page with neither carries nothing of either.</summary>
    [Fact]
    public void APageWithNeitherCarriesNothing()
    {
        using var session = Start(string.Empty, "<dialog id=\"d\">closed</dialog><div id=\"p\" popover>hidden</div><p id=\"t\">text</p>");

        var html = session.CurrentHtml();

        Assert.DoesNotContain("data-broiler-top-layer", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-broiler-backdrop", html, StringComparison.Ordinal);
    }

    /// <summary>The start tag of the element with <paramref name="id"/>.</summary>
    private static string Tag(string html, string id)
    {
        var at = html.IndexOf($"id=\"{id}\"", StringComparison.Ordinal);
        Assert.True(at >= 0, $"No element {id} in: {html}");
        var open = html.LastIndexOf('<', at);
        var close = html.IndexOf('>', at);
        return html[open..(close + 1)];
    }

    private static string? Attribute(string tag, string name)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            tag, $"\\s{System.Text.RegularExpressions.Regex.Escape(name)}=\"([^\"]*)\"");
        return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value) : null;
    }
}
