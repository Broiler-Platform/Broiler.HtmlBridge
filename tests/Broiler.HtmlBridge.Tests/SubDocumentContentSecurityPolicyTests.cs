using System.Net;
using System.Net.Sockets;
using System.Text;

using Broiler.HtmlBridge;

namespace Broiler.Browser.Core.Tests;

/// <summary>
/// Which Content-Security-Policy governs a nested browsing context's scripts, and whether one is
/// consulted at all.
/// <para>
/// <b>The sub-document path looks policed and mostly is not.</b> A frame's scripts reach
/// <c>ScriptExtractionService.ExtractAll</c>, which derives a policy with
/// <c>ContentSecurityPolicy.FromHtml(html)</c> — from the FRAME'S OWN MARKUP — and then gates each
/// body on <c>csp == null || csp.AllowsInlineScript(...)</c>. <c>FromHtml</c> answers
/// <see langword="null"/> for markup carrying no <c>&lt;meta http-equiv&gt;</c>, and a null policy
/// admits everything. So for the ordinary frame — one declaring no policy of its own, which is
/// nearly all of them — the gate is a null test that passes, and the code reads as though a
/// decision was taken when none was.
/// </para>
/// <para>
/// <b>What the specification asks for.</b> A document fetched over the network carries its own
/// policy, in a response header or a meta element. A document with a LOCAL SCHEME —
/// <c>about:srcdoc</c>, <c>about:blank</c>, <c>data:</c>, <c>blob:</c> — has no response of its own
/// to carry one and INHERITS THE EMBEDDER'S. Inherits, not replaces: a frame that declares a policy
/// is bound by both, so a permissive <c>&lt;meta&gt;</c> inside a frame cannot buy back what the
/// embedder forbade. That half is worth its own test, because getting it wrong turns srcdoc into a
/// way out of the page's policy rather than a document inside it.
/// </para>
/// <para>
/// <b>Every prohibition here is paired with a control that runs the same frame under no policy at
/// all.</b> Two of these tests passed for the wrong reason before those controls existed: one frame
/// declared <c>script-src *</c>, which does not admit an inline script, so it blocked its own script
/// and the embedder's policy was never consulted; and the XML case asserted a script had not run on
/// a path where it never runs. A prohibition test with no control cannot tell "forbidden" from
/// "never happened".
/// </para>
/// </summary>
public class SubDocumentContentSecurityPolicyTests
{
    private const string PageUrl = "https://example.test/frames";

    /// <summary>
    /// Runs <paramref name="script"/> against <paramref name="pageHtml"/> and returns what it wrote
    /// to <c>#out</c>, which is how the frame suites already read a result back out of a render.
    /// </summary>
    /// <param name="pageUrl">
    /// The document's own URL. It matters for the network frames below and nowhere else:
    /// <c>contentDocument</c> answers <see langword="null"/> for a cross-origin frame
    /// (<c>IframeElementBinding</c> asks <c>IsCurrentIframeCrossOrigin</c> first), so a test reading
    /// a loopback frame's document has to serve its page from that same origin or it reads nothing
    /// and learns nothing about policy.
    /// </param>
    private static string Run(string pageHtml, string script, string? pageUrl = null)
    {
        var html = new ScriptEngine().Execute(
            [$"document.getElementById('out').textContent = String({script});"],
            pageHtml,
            pageUrl ?? PageUrl);

        Assert.NotNull(html);

        const string open = "<div id=\"out\">";
        var start = html!.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no #out div in serialized output: {html}");
        start += open.Length;
        var end = html.IndexOf("</div>", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"unterminated #out div in serialized output: {html}");
        return html[start..end];
    }

    /// <summary>
    /// A page carrying <paramref name="policy"/> as its own meta policy, embedding
    /// <paramref name="frameMarkup"/>, plus the <c>#out</c> sink the probe writes into.
    /// </summary>
    private static string PageWithPolicy(string? policy, string frameMarkup) =>
        "<html><head>" +
        (policy is null
            ? string.Empty
            : $"<meta http-equiv=\"Content-Security-Policy\" content=\"{policy}\">") +
        "</head><body>" +
        frameMarkup +
        "<div id=\"out\"></div>" +
        "</body></html>";

    // ---------------------------------------------------------------------
    //  HTML frames: srcdoc and data:, which run against the frame's own window
    // ---------------------------------------------------------------------

    // The frame's script records that it ran by writing into the frame's own DOM. Single quotes
    // throughout, so the whole string sits inside a double-quoted srcdoc attribute unescaped.
    private const string FrameBody =
        "<p id='ran'>no</p><script>document.getElementById('ran').textContent = 'yes';</script>";

    /// <summary>
    /// The same frame, preceded by a meta policy of its own that genuinely permits its inline
    /// script. The meta's own attribute delimiters are <c>&amp;quot;</c> so that one round of
    /// attribute-value decoding leaves real double quotes around a policy whose
    /// <c>'unsafe-inline'</c> keeps its single quotes — the keyword is nothing without them.
    /// </summary>
    private const string FrameBodyDeclaringUnsafeInline =
        "<meta http-equiv=&quot;Content-Security-Policy&quot; content=&quot;script-src 'unsafe-inline'&quot;>" +
        FrameBody;

    /// <summary>Reads the frame's own marker back through <c>contentDocument</c>.</summary>
    private const string ReadFrameMarker =
        "(function () {" +
        "  var d = document.getElementById('f').contentDocument;" +
        "  var p = d && d.getElementById('ran');" +
        "  return p ? p.textContent : 'no-frame';" +
        "})()";

    /// <summary>
    /// CONSEQUENCE 1. A <c>srcdoc</c> frame is <c>about:srcdoc</c> — a local scheme with no response
    /// of its own — so it inherits the embedder's policy, which here forbids script outright.
    /// </summary>
    [Fact]
    public void ASrcdocFrameInheritsTheEmbeddersPolicy()
    {
        var page = PageWithPolicy("script-src 'none'", $"<iframe id=\"f\" srcdoc=\"{FrameBody}\"></iframe>");

        Assert.Equal("no", Run(page, ReadFrameMarker));
    }

    /// <summary>The control: the same frame runs when the embedder states no policy.</summary>
    [Fact]
    public void ASrcdocFrameRunsItsScriptWhenTheEmbedderIsSilent()
    {
        var page = PageWithPolicy(null, $"<iframe id=\"f\" srcdoc=\"{FrameBody}\"></iframe>");

        Assert.Equal("yes", Run(page, ReadFrameMarker));
    }

    /// <summary>
    /// CONSEQUENCE 1, the half that decides whether inheriting is worth anything: the frame declares
    /// a policy that permits its script and the embedder forbids it. Both apply, so it must not run.
    /// </summary>
    [Fact]
    public void ASrcdocFrameCannotWidenTheEmbeddersPolicyWithItsOwnMeta()
    {
        var page = PageWithPolicy(
            "script-src 'none'",
            $"<iframe id=\"f\" srcdoc=\"{FrameBodyDeclaringUnsafeInline}\"></iframe>");

        Assert.Equal("no", Run(page, ReadFrameMarker));
    }

    /// <summary>
    /// The control that makes the test above mean something: the frame's own policy really is
    /// permissive, so with a silent embedder that same frame runs its script. Without this, a frame
    /// whose meta accidentally forbade its own script would satisfy the test above having consulted
    /// nothing.
    /// </summary>
    [Fact]
    public void ASrcdocFrameDeclaringUnsafeInlineRunsItsScriptWhenTheEmbedderIsSilent()
    {
        var page = PageWithPolicy(null, $"<iframe id=\"f\" srcdoc=\"{FrameBodyDeclaringUnsafeInline}\"></iframe>");

        Assert.Equal("yes", Run(page, ReadFrameMarker));
    }

    /// <summary>
    /// <c>data:</c> is the other local scheme this path builds an HTML document for, and it inherits
    /// for the same reason <c>about:srcdoc</c> does.
    /// </summary>
    [Fact]
    public void ADataUriFrameInheritsTheEmbeddersPolicy()
    {
        var page = PageWithPolicy(
            "script-src 'none'",
            $"<iframe id=\"f\" src=\"data:text/html,{FrameBody}\"></iframe>");

        Assert.Equal("no", Run(page, ReadFrameMarker));
    }

    /// <summary>The control for the <c>data:</c> case.</summary>
    [Fact]
    public void ADataUriFrameRunsItsScriptWhenTheEmbedderIsSilent()
    {
        var page = PageWithPolicy(null, $"<iframe id=\"f\" src=\"data:text/html,{FrameBody}\"></iframe>");

        Assert.Equal("yes", Run(page, ReadFrameMarker));
    }

    // ---------------------------------------------------------------------
    //  XML sub-documents, which run against the PAGE's window
    // ---------------------------------------------------------------------

    /// <summary>
    /// An XHTML sub-document's scripts are run by <c>SubDocuments.XmlAndScripts.cs</c>, which — unlike
    /// the HTML path — evaluates them without a window context, so <c>document</c> inside one of them
    /// is the PAGE's document and not the frame's. The marker is therefore a global on the page's
    /// window: writing into the frame's own DOM, as the HTML frames above do, would throw inside the
    /// frame and be swallowed as a warning, which is a way for this test to pass having proved
    /// nothing.
    /// </summary>
    private const string XhtmlFrame =
        "<html xmlns='http://www.w3.org/1999/xhtml'><body>" +
        "<p id='ran'>no</p>" +
        "<script>window.__xmlFrameRan = 'yes';</script>" +
        "</body></html>";

    /// <remarks>
    /// It reaches for <c>contentDocument</c> first and throws the answer away. A sub-document is
    /// built lazily, on the first access to the frame's document, so a probe that only read the
    /// global never caused the frame to exist and reported "did not run" for every input — a control
    /// that fails for the same reason its prohibition passes.
    /// </remarks>
    private const string ReadXmlMarker =
        "(function () {" +
        "  var d = document.getElementById('f').contentDocument;" +
        "  return d ? (window.__xmlFrameRan || 'no') : 'no-frame';" +
        "})()";

    /// <summary>
    /// CONSEQUENCE 3. That path walks the built tree and evaluates every script element's text
    /// without consulting any policy at all — not a null policy that admits everything: no policy is
    /// looked for.
    /// </summary>
    [Fact]
    public void AnXmlSubDocumentIsGovernedByAPolicyAtAll()
    {
        var page = PageWithPolicy(
            "script-src 'none'",
            $"<iframe id=\"f\" src=\"data:application/xhtml+xml,{XhtmlFrame}\"></iframe>");

        Assert.Equal("no", Run(page, ReadXmlMarker));
    }

    /// <summary>
    /// The control, and the one that decides whether the test above judges anything: with no policy
    /// on the page the same sub-document must run its script. An XML path that never ran scripts —
    /// or a data URI that never parsed — would otherwise satisfy that test by doing nothing.
    /// </summary>
    [Fact]
    public void AnXmlSubDocumentRunsItsScriptWhenNoPolicyForbidsIt()
    {
        var page = PageWithPolicy(null, $"<iframe id=\"f\" src=\"data:application/xhtml+xml,{XhtmlFrame}\"></iframe>");

        Assert.Equal("yes", Run(page, ReadXmlMarker));
    }

    // ---------------------------------------------------------------------
    //  Network frames, whose policy arrives in a response header
    // ---------------------------------------------------------------------

    /// <summary>
    /// A single-purpose HTTP origin on the loopback interface, serving one body with one optional
    /// <c>Content-Security-Policy</c> header.
    /// </summary>
    /// <remarks>
    /// Written on a raw <see cref="TcpListener"/> rather than <c>HttpListener</c> because the latter
    /// needs a URL reservation on Windows for anything but an elevated process, which is a way for
    /// this test to fail on a developer's machine for a reason that has nothing to do with what it
    /// asserts. The port is chosen by the OS, so parallel test runs cannot collide.
    /// </remarks>
    private sealed class LoopbackOrigin : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stopping = new();

        public LoopbackOrigin(string body, string contentType, string? contentSecurityPolicy)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var origin = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            Url = origin + "/frame";
            PageUrl = origin + "/page";

            var payload = Encoding.UTF8.GetBytes(body);
            var head = new StringBuilder()
                .Append("HTTP/1.1 200 OK\r\n")
                .Append($"Content-Type: {contentType}\r\n")
                .Append($"Content-Length: {payload.Length}\r\n");
            if (contentSecurityPolicy is not null)
                head.Append($"Content-Security-Policy: {contentSecurityPolicy}\r\n");
            var header = Encoding.ASCII.GetBytes(head.Append("Connection: close\r\n\r\n").ToString());

            _ = Task.Run(() => ServeAsync(header, payload));
        }

        /// <summary>The frame's URL.</summary>
        public string Url { get; }

        /// <summary>A URL on the same origin, for the page that embeds the frame.</summary>
        public string PageUrl { get; }

        private async Task ServeAsync(byte[] header, byte[] payload)
        {
            while (!_stopping.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                }
                catch (OperationCanceledException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { return; }

                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();

                        // Drain the request line and headers. Answering before the client has
                        // finished sending can surface as a connection reset rather than a response.
                        var request = new byte[4096];
                        var read = await stream.ReadAsync(request, _stopping.Token);
                        if (read <= 0)
                            continue;

                        await stream.WriteAsync(header, _stopping.Token);
                        await stream.WriteAsync(payload, _stopping.Token);
                        await stream.FlushAsync(_stopping.Token);
                    }
                    catch (Exception) { /* the client hung up; nothing here is worth failing a test over */ }
                }
            }
        }

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Stop();
            _stopping.Dispose();
        }
    }

    /// <summary>
    /// CONSEQUENCE 2. A frame fetched over the network is bound by the policy its RESPONSE HEADER
    /// delivered. The page states no policy at all here, so the header is the only thing that can
    /// forbid the frame's script — which makes this a test of whether the header is read, and of
    /// nothing else.
    /// </summary>
    [Fact]
    public void ANetworkFrameIsBoundByThePolicyItsResponseHeaderDelivered()
    {
        using var origin = new LoopbackOrigin(
            $"<html><body>{FrameBody}</body></html>", "text/html", "script-src 'none'");

        var page = PageWithPolicy(null, $"<iframe id=\"f\" src=\"{origin.Url}\"></iframe>");

        Assert.Equal("no", Run(page, ReadFrameMarker, origin.PageUrl));
    }

    /// <summary>
    /// The control, and here it carries more than usual: it says the loopback origin is reachable
    /// and its body parsed. Without it, a frame that failed to fetch would satisfy the test above
    /// by producing an empty document, and the header would never have been read at all.
    /// </summary>
    [Fact]
    public void ANetworkFrameRunsItsScriptWhenItsResponseDeliversNoPolicy()
    {
        using var origin = new LoopbackOrigin(
            $"<html><body>{FrameBody}</body></html>", "text/html", contentSecurityPolicy: null);

        var page = PageWithPolicy(null, $"<iframe id=\"f\" src=\"{origin.Url}\"></iframe>");

        Assert.Equal("yes", Run(page, ReadFrameMarker, origin.PageUrl));
    }

    /// <summary>
    /// A network document does NOT inherit its embedder's policy — it is bound by what its own
    /// response and markup say. So a page forbidding script does not reach into a frame served from
    /// an origin that permits it, which is the boundary the local-scheme tests above sit on the
    /// other side of.
    /// </summary>
    [Fact]
    public void ANetworkFrameDoesNotInheritTheEmbeddersPolicy()
    {
        using var origin = new LoopbackOrigin(
            $"<html><body>{FrameBody}</body></html>", "text/html", contentSecurityPolicy: null);

        var page = PageWithPolicy("script-src 'none'", $"<iframe id=\"f\" src=\"{origin.Url}\"></iframe>");

        Assert.Equal("yes", Run(page, ReadFrameMarker, origin.PageUrl));
    }
}
