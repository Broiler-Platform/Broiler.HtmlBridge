using System.Net;
using System.Text.Json;
using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// <c>isSecureContext</c>, on a page, in its frames and in a worker, and the <c>crypto.subtle</c> that
/// goes with it. Every expectation is Chromium's, measured with the same documents.
/// </summary>
/// <remarks>
/// The bridge had no <c>isSecureContext</c>: it read as <c>undefined</c>, which reCAPTCHA reads before
/// it looks at <c>navigator.mediaDevices</c>. And a <c>data:</c> document counted as secure, where
/// Chromium gives it -- and every document inside it -- neither <c>isSecureContext</c> nor
/// <c>crypto.subtle</c>.
/// </remarks>
public class SecureContextTests
{
    private const string Out = "<div id=\"out\"></div>";

    private static string Run(string body, string script, string url)
    {
        var rendered = new ScriptEngine().Execute(
            ["function done(v) { document.getElementById('out').textContent = String(v); }", script],
            $"<!DOCTYPE html><html><head></head><body>{Out}{body}</body></html>", url);
        Assert.NotNull(rendered);
        return PageProbe.OutOf(rendered!, decode: true);
    }

    /// <summary>
    /// A page is a secure context at an HTTPS URL, or an HTTP one on a loopback address or a localhost
    /// name, and not at any other HTTP URL. <c>isSecureContext</c> is a getter of the window's own,
    /// enumerable and configurable, with no setter.
    /// </summary>
    [Theory]
    [InlineData("https://example.test/page", "true,object")]
    [InlineData("http://127.0.0.1:8000/page", "true,object")]
    [InlineData("http://localhost:8000/page", "true,object")]
    [InlineData("http://example.test/page", "false,undefined")]
    public void APageIsASecureContextAtATrustworthyUrl(string url, string expected)
    {
        Assert.Equal(expected, Run("", "done([isSecureContext, typeof crypto.subtle]);", url));
        Assert.Equal("function,undefined,true,true", Run("",
            "var d = Object.getOwnPropertyDescriptor(window, 'isSecureContext');" +
            "done([typeof d.get, typeof d.set, d.enumerable, d.configurable]);", url));
    }

    /// <summary>
    /// A frame answers for its own document: <c>isSecureContext</c>, <c>window.isSecureContext</c> and
    /// its <c>crypto.subtle</c>. A <c>srcdoc</c> frame is its page's, sandboxed or not; a <c>data:</c>
    /// frame is not secure, and nor is a frame inside one. On an insecure page no frame is.
    /// </summary>
    [Theory]
    [InlineData("https://example.test/page", "srcdoc", "true,true,object")]
    [InlineData("https://example.test/page", "sandboxed srcdoc", "true,true,object")]
    [InlineData("https://example.test/page", "data", "false,false,undefined")]
    [InlineData("https://example.test/page", "srcdoc inside data", "false,false,undefined")]
    [InlineData("http://example.test/page", "srcdoc", "false,false,undefined")]
    public void AFrameAnswersForItsOwnDocument(string url, string kind, string expected)
    {
        const string probe =
            "<script>top.postMessage([isSecureContext, window.isSecureContext, typeof crypto.subtle].join(), '*');</script>";
        var holder = $"<iframe id=\"c\" srcdoc=\"{WebUtility.HtmlEncode(probe)}\"></iframe>" +
                     "<script>var touch = document.getElementById('c').contentWindow;</script>";
        var frame = kind switch
        {
            "srcdoc" => $"<iframe id=\"f\" srcdoc=\"{WebUtility.HtmlEncode(probe)}\"></iframe>",
            "sandboxed srcdoc" => $"<iframe id=\"f\" sandbox=\"allow-scripts\" srcdoc=\"{WebUtility.HtmlEncode(probe)}\"></iframe>",
            "data" => $"<iframe id=\"f\" src=\"data:text/html,{Uri.EscapeDataString(probe)}\"></iframe>",
            _ => $"<iframe id=\"f\" src=\"data:text/html,{Uri.EscapeDataString(holder)}\"></iframe>",
        };

        // A frame of another origin loads when something needs its window.
        Assert.Equal(expected, Run(frame,
            "window.addEventListener('message', function (e) { done(e.data); });" +
            "var touch = document.getElementById('f').contentWindow;", url));
    }

    /// <summary>
    /// The page reads a frame's answer off its window, and a frame reads the page's off <c>top</c>.
    /// </summary>
    [Fact]
    public void AWindowAnswersForItsDocumentWhoeverAsks()
    {
        const string probe = "<script>parent.document.getElementById('top').textContent = top.isSecureContext;</script>";
        Assert.Equal("true,true,true", Run(
            $"<span id=\"top\"></span><iframe id=\"f\" srcdoc=\"{WebUtility.HtmlEncode(probe)}\"></iframe><iframe id=\"b\"></iframe>",
            "window.addEventListener('load', function () { done([document.getElementById('f').contentWindow.isSecureContext," +
            " document.getElementById('b').contentWindow.isSecureContext, document.getElementById('top').textContent]); });",
            "https://example.test/page"));
    }

    /// <summary>
    /// A worker is as secure as the page that started it, from a <c>data:</c> URL too, and answers with
    /// a getter on its global.
    /// </summary>
    [Theory]
    [InlineData("https://example.test/page", "true,object,function")]
    [InlineData("http://example.test/page", "false,undefined,function")]
    public void AWorkerIsAsSecureAsItsPage(string url, string expected)
    {
        const string worker =
            "var d = Object.getOwnPropertyDescriptor(self, 'isSecureContext');" +
            "postMessage([self.isSecureContext, typeof crypto.subtle, d && typeof d.get].join());";
        Assert.Equal(expected, WorkerProbe.Answer(
            $"new Worker('data:text/javascript,' + encodeURIComponent({JsonSerializer.Serialize(worker)}))", url));
    }
}
