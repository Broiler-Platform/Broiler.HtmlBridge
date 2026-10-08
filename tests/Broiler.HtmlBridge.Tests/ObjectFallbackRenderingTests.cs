using Broiler.HtmlBridge.Dom;
using Broiler.Net.Http;
using Reply = Broiler.HtmlBridge.Tests.LoopbackCookieServer.Reply;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// An <c>&lt;object&gt;</c> renders its fallback content only when its data did not load as
/// something it can show (HTML §4.8.7): the renderer is handed what the bridge loaded, since the
/// markup need not say.
/// </summary>
/// <remarks>
/// <para>
/// <b>Acid3 drew "FAIL" above its heading.</b> Its test 16 appends <c>support-a.png</c> (a 404)
/// holding <c>support-b.png</c> (a 200 <c>text/html</c> page) holding <c>support-c.png</c> (an
/// <c>image/png</c>) holding the text "FAIL". The second object's data loaded, so it shows its page
/// and none of its children. The bridge knew each outcome, but the render projection carried none of
/// it, and the renderer, deciding from the markup, drew every object and the text.
/// </para>
/// <para>
/// The page is on <c>localhost</c>, served by a loopback server; what is asserted is the markup the
/// renderer is handed.
/// </para>
/// </remarks>
public class ObjectFallbackRenderingTests
{
    private const string Fallback = "FALLBACK-TEXT";

    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAD0lEQVR4nGNg+M8AQhAKABvyA/1tVLjHAAAAAElFTkSuQmCC");

    private static LoopbackCookieServer Serve() =>
        new LoopbackCookieServer()
            .Map("/a", new Reply(404, "image/png", BodyBytes: Png))
            .Map("/b", new Reply(Body:
                "<!DOCTYPE html><html><head><title>PAGE</title><style> * { background: transparent; } </style></head>" +
                "<body><p><!-- this file is transparent --></p></body></html>"))
            .Map("/c", new Reply(ContentType: "image/png", BodyBytes: Png));

    /// <summary>The markup handed to the renderer for a page at <c>localhost</c> holding
    /// <paramref name="body"/>, once <paramref name="script"/> has run and the page has settled.</summary>
    private static string Rendered(LoopbackCookieServer server, string body, string script = "")
    {
        using var profile = new BrowserNetworkSession(new BrowserNetworkSessionOptions { Timeout = TimeSpan.FromSeconds(20) });
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions { Network = profile, Cookies = profile }));
        using var session = engine.ExecuteInteractive(
            [script], [], $"<!DOCTYPE html><html><head></head><body>{body}</body></html>", server.LocalhostUrl("/page"));
        return session!.SettleLoadWindow();
    }

    /// <summary>The start tag of the element with <paramref name="id"/>.</summary>
    private static string StartTag(string html, string id) =>
        System.Text.RegularExpressions.Regex.Match(html, $"<object[^>]*\\bid=\"{id}\"[^>]*>").Value;

    /// <summary>Control, which passes before and after: an object whose data is a 404 renders its fallback.</summary>
    [Fact]
    public void AnObjectWhoseDataFailedRendersItsFallback()
    {
        using var server = Serve();
        var html = Rendered(server, $"<object id=\"o\" data=\"/a\"><span>{Fallback}</span></object>");

        Assert.Contains(Fallback, html);
        Assert.DoesNotContain("data-broiler-object-type", StartTag(html, "o"));
        Assert.DoesNotContain("data-broiler-frame-document", StartTag(html, "o"));
    }

    /// <summary>
    /// An object whose data is a page renders the page and not its fallback, though neither its URL
    /// nor a <c>type</c> says it is one: its document is stamped for the renderer to draw, as a
    /// frame's is.
    /// </summary>
    [Fact]
    public void AnObjectWhoseDataIsADocumentRendersItInsteadOfItsFallback()
    {
        using var server = Serve();
        var html = Rendered(server, $"<object id=\"o\" data=\"/b\"><span>{Fallback}</span></object>");

        Assert.DoesNotContain(Fallback, html);
        var tag = StartTag(html, "o");
        Assert.Contains("data-broiler-object-type=\"text/html\"", tag);
        Assert.Contains("data-broiler-frame-document=", tag);
        Assert.Contains("PAGE", tag);
    }

    /// <summary>
    /// An object whose data is an image renders it as an image and not its fallback, though neither
    /// its URL nor a <c>type</c> says it is one; its <c>type</c> stays the author's.
    /// </summary>
    [Fact]
    public void AnObjectWhoseDataIsAnImageRendersItInsteadOfItsFallback()
    {
        using var server = Serve();
        var html = Rendered(server, $"<object id=\"o\" data=\"/c\"><span>{Fallback}</span></object>");

        Assert.DoesNotContain(Fallback, html);
        var tag = StartTag(html, "o");
        Assert.Contains("data-broiler-object-type=\"image/png\"", tag);
        Assert.DoesNotContain(" type=", tag);
    }

    /// <summary>
    /// Acid3's test 16, appended by script after the page loaded: the 404 shows its fallback, the
    /// page; the page shows no fallback; so neither the image object nor "FAIL" renders, and the
    /// image object, never rendered, is never loaded either.
    /// </summary>
    [Fact]
    public void Acid3NestedObjectsRenderNoFail()
    {
        using var server = Serve();
        var html = Rendered(server, "<map></map>",
            """
            window.addEventListener('load', function () {
              setTimeout(function () {
                var oC = document.createElement('object');
                oC.appendChild(document.createTextNode('FAIL'));
                var oB = document.createElement('object');
                var oA = document.createElement('object');
                oA.id = 'a'; oB.id = 'b'; oC.id = 'c';
                oA.data = '/a';
                oB.data = '/b';
                oB.appendChild(oC);
                oC.data = '/c';
                oA.appendChild(oB);
                document.getElementsByTagName('map')[0].appendChild(oA);
              }, 0);
            });
            """);

        Assert.DoesNotContain("FAIL", html);
        Assert.Matches("<object[^>]*\\bid=\"a\"[^>]*><object[^>]*\\bid=\"b\"[^>]*></object></object>", html);
        Assert.DoesNotContain("data-broiler-object-type", StartTag(html, "a"));
        Assert.Contains("data-broiler-frame-document=", StartTag(html, "b"));
        Assert.Empty(server.RequestsFor("/c"));
    }

    /// <summary>
    /// The same nesting in the page's markup, loaded with the page: what shows is decided the same
    /// way, from the outermost object in.
    /// </summary>
    [Fact]
    public void NestedObjectsInMarkupRenderOnlyWhatShows()
    {
        using var server = Serve();
        var html = Rendered(server,
            "<object id=\"a\" data=\"/a\"><object id=\"b\" data=\"/b\"><object id=\"c\" data=\"/c\">FAIL</object></object></object>");

        Assert.DoesNotContain("FAIL", html);
        Assert.Matches("<object[^>]*\\bid=\"a\"[^>]*><object[^>]*\\bid=\"b\"[^>]*></object></object>", html);
        Assert.Empty(server.RequestsFor("/c"));
    }

    /// <summary>
    /// Acid2's eyes: an unknown type and a 404 fall back, and the <c>data:image</c> innermost is the
    /// image, without its fallback. No <c>type</c> is written, so that Acid2's <c>object[type]</c>
    /// still matches only the object that has one.
    /// </summary>
    [Fact]
    public void Acid2EyesFallBackToTheImage()
    {
        using var server = Serve();
        var html = Rendered(server,
            "<object id=\"a\" data=\"data:application/x-unknown,ERROR\"><object id=\"b\" data=\"/a\" type=\"text/html\">" +
            "<object id=\"c\" data=\"data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAD0lEQVR4nGNg+M8AQhAKABvyA/1tVLjHAAAAAElFTkSuQmCC\">" +
            "<span>ERROR-TEXT</span></object></object></object>");

        Assert.DoesNotContain("ERROR-TEXT", html);
        Assert.Contains("<object id=\"c\"", html);
        Assert.Contains("type=\"text/html\"", StartTag(html, "b"));
        Assert.DoesNotContain("data-broiler-object-type", StartTag(html, "a"));
        Assert.DoesNotContain("data-broiler-object-type", StartTag(html, "b"));
    }
}
