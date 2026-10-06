using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom;
using Broiler.Net.Http;
using Reply = Broiler.HtmlBridge.Tests.LoopbackCookieServer.Reply;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A submission into a frame loads its answer in that frame, as Chromium's does: a form whose target names
/// the frame, and a form in the frame's own document; the page stays where it is.
/// </summary>
/// <remarks>
/// <para>
/// <b>Neither went anywhere.</b> A submission into a frame was logged and dropped, and a form in a frame did
/// nothing at all, since the host finds forms by their place in the page's document.
/// </para>
/// <para>
/// Measured in Chromium: a GET form whose target names a frame loads
/// <c>/?q=v</c> in the frame, and the page's URL is unchanged.
/// </para>
/// </remarks>
public class FrameSubmissionTests
{
    private static Reply Answer(LoopbackCookieServer.Request request) =>
        new(Body: $"<html><body><p id=\"answer\">{request.Method} {request.Path} {request.Header("Content-Type")} {request.Body}</p></body></html>");

    /// <summary>A page at <c>localhost</c>, its profile, and the session its scripts run in.</summary>
    private sealed class Page : IDisposable
    {
        public Page(LoopbackCookieServer server, string body, string script)
        {
            Profile = new BrowserNetworkSession(new BrowserNetworkSessionOptions { Timeout = TimeSpan.FromSeconds(20) });
            Session = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions { Network = Profile, Cookies = Profile }))
                .ExecuteInteractive(
                    ["var out = document.getElementById('out');" +
                     "function frameText() { var d = document.getElementById('sink').contentDocument; var a = d && d.getElementById('answer'); return a ? a.textContent : 'none'; }" +
                     script],
                    [], $"<!DOCTYPE html><html><body><div id=\"out\">waiting</div>{body}</body></html>", server.LocalhostUrl("/page"))!;
            Assert.NotNull(Session);
        }

        public BrowserNetworkSession Profile { get; }

        public InteractiveSession Session { get; }

        public string Settle() => PageProbe.OutOf(Session.SettleLoadWindow(), decode: true);

        public void Dispose()
        {
            Session.Dispose();
            Profile.Dispose();
        }
    }

    private static LoopbackCookieServer Server() =>
        new LoopbackCookieServer()
            .Map("/start", new Reply(Body: "<html><body><p>start</p></body></html>"))
            .Map("/result", Answer);

    /// <summary>A GET form whose target names a frame loads the answer in the frame, its entries in the query; the page stays.</summary>
    [Fact]
    public void AGetFormTargetingAFrameLoadsInTheFrame()
    {
        using var server = Server();
        using var page = new Page(server,
            $"<iframe name=\"sink\" id=\"sink\" src=\"{server.LocalhostUrl("/start")}\"></iframe>" +
            "<form id=\"f\" action=\"/result#top\" target=\"sink\"><input name=\"q\" value=\"v w\"><input type=\"file\" name=\"up\"></form>",
            "document.getElementById('sink').contentWindow;" +
            "document.getElementById('sink').addEventListener('load', function () { out.textContent = frameText(); });" +
            "document.getElementById('f').submit();");

        Assert.Equal("GET /result?q=v+w&up=", page.Settle().TrimEnd());
        Assert.Equal("/result?q=v+w&up=", server.Single("/result").Path);
        Assert.Null(page.Session.TakePendingNavigation());
    }

    /// <summary>A POST into a frame sends its entries as its encoding says: URL-encoded, or plain text.</summary>
    [Theory]
    [InlineData("", "application/x-www-form-urlencoded", "q=v+w&up=")]
    [InlineData("text/plain", "text/plain", "q=v w\r\nup=\r\n")]
    public void APostIntoAFrameSendsItsBody(string enctype, string contentType, string body)
    {
        using var server = Server();
        using var page = new Page(server,
            $"<iframe name=\"sink\" id=\"sink\" src=\"{server.LocalhostUrl("/start")}\"></iframe>" +
            $"<form id=\"f\" action=\"/result\" method=\"post\" enctype=\"{enctype}\" target=\"sink\"><input name=\"q\" value=\"v w\"><input type=\"file\" name=\"up\"></form>",
            "document.getElementById('sink').contentWindow; document.getElementById('f').submit();");
        page.Settle();

        var sent = server.Single("/result");
        Assert.Equal("POST", sent.Method);
        Assert.StartsWith(contentType, sent.Header("Content-Type"));
        Assert.Equal(body, sent.Body);
        Assert.Null(page.Session.TakePendingNavigation());
    }

    /// <summary>A multipart POST into a frame sends a text part per field and an empty file part for a file input with nothing chosen.</summary>
    [Fact]
    public void AMultipartPostIntoAFrameSendsItsParts()
    {
        using var server = Server();
        using var page = new Page(server,
            $"<iframe name=\"sink\" id=\"sink\" src=\"{server.LocalhostUrl("/start")}\"></iframe>" +
            "<form id=\"f\" action=\"/result\" method=\"post\" enctype=\"multipart/form-data\" target=\"sink\"><input name=\"q\" value=\"v\"><input type=\"file\" name=\"up\"></form>",
            "document.getElementById('sink').contentWindow; document.getElementById('f').submit();");
        page.Settle();

        var sent = server.Single("/result");
        var boundary = sent.Header("Content-Type")!.Split("boundary=")[1];
        Assert.Equal(
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"q\"\r\n\r\nv\r\n" +
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"up\"; filename=\"\"\r\nContent-Type: application/octet-stream\r\n\r\n\r\n" +
            $"--{boundary}--\r\n",
            sent.Body);
    }

    /// <summary>A form in a frame's document submits into that frame, its action resolved against the frame's URL.</summary>
    [Fact]
    public void AFormInAFrameSubmitsIntoItsFrame()
    {
        using var server = new LoopbackCookieServer()
            .Map("/inner/start", new Reply(Body:
                "<html><body><form id=\"inner\" action=\"result\"><input name=\"x\" value=\"1\"></form></body></html>"))
            .Map("/inner/result", Answer);
        using var page = new Page(server,
            $"<iframe name=\"sink\" id=\"sink\" src=\"{server.LocalhostUrl("/inner/start")}\"></iframe>",
            "var frame = document.getElementById('sink'); frame.contentWindow;" +
            "frame.addEventListener('load', function () { out.textContent = frameText(); });" +
            "frame.contentDocument.getElementById('inner').submit();");

        Assert.Equal("GET /inner/result?x=1", page.Settle().TrimEnd());
        Assert.Equal("/inner/result?x=1", server.Single("/inner/result").Path);
        Assert.Null(page.Session.TakePendingNavigation());
    }

    /// <summary>A frame's GET form that targets the page is the page's navigation, with its entries in the URL.</summary>
    [Fact]
    public void AFramesFormIntoThePageIsThePagesNavigation()
    {
        using var server = new LoopbackCookieServer()
            .Map("/inner/start", new Reply(Body:
                "<html><body><form id=\"up\" action=\"/top\" target=\"_top\"><input name=\"x\" value=\"1\"></form></body></html>"));

        using var page = new Page(server,
            $"<iframe id=\"sink\" src=\"{server.LocalhostUrl("/inner/start")}\"></iframe>",
            "document.getElementById('sink').contentWindow.document.getElementById('up').submit();");
        page.Settle();

        var pending = page.Session.TakePendingNavigation();
        Assert.Equal(NavigationKind.FormSubmit, pending?.Kind);
        Assert.Equal(server.LocalhostUrl("/top?x=1"), pending!.Url);
        Assert.Equal(-1, pending.FormIndex);
        Assert.Null(pending.Body);
    }

    /// <summary>
    /// A frame's POST form that targets the page is the page's navigation too, to its action, with its entries
    /// encoded as its body and the frame's document as its initiator. It was dropped: the host submits the
    /// page's own forms, which it finds by their place in the page's document.
    /// </summary>
    [Theory]
    [InlineData("", "application/x-www-form-urlencoded", "x=2+3")]
    [InlineData("text/plain", "text/plain", "x=2 3\r\n")]
    public void AFramesPostIntoThePageCarriesItsBody(string enctype, string contentType, string body)
    {
        using var server = new LoopbackCookieServer()
            .Map("/inner/start", new Reply(Body:
                $"<html><body><form id=\"posted\" action=\"/top\" method=\"post\" enctype=\"{enctype}\" target=\"_top\"><input name=\"x\" value=\"2 3\"></form></body></html>"));

        using var page = new Page(server,
            $"<iframe id=\"sink\" src=\"{server.LocalhostUrl("/inner/start")}\"></iframe>",
            "document.getElementById('sink').contentWindow.document.getElementById('posted').submit();");
        page.Settle();

        var pending = page.Session.TakePendingNavigation();
        Assert.Equal(NavigationKind.FormSubmit, pending?.Kind);
        Assert.Equal(server.LocalhostUrl("/top"), pending!.Url);
        Assert.Equal(-1, pending.FormIndex);
        Assert.Equal(contentType, pending.BodyContentType);
        Assert.Equal(body, System.Text.Encoding.UTF8.GetString(pending.Body!));
        Assert.Equal(server.LocalhostUrl("/inner/start"), pending.Initiator?.DocumentUrl.ToString());
    }

    /// <summary>A multipart POST into a frame sends the file the user chose: its name, its type and its bytes.</summary>
    [Fact]
    public void AMultipartPostIntoAFrameSendsTheChosenFile()
    {
        using var server = Server();
        using var page = new Page(server,
            $"<iframe name=\"sink\" id=\"sink\" src=\"{server.LocalhostUrl("/start")}\"></iframe>" +
            "<form id=\"f\" action=\"/result\" method=\"post\" enctype=\"multipart/form-data\" target=\"sink\"><input name=\"q\" value=\"v\"><input type=\"file\" id=\"up\" name=\"up\"></form>",
            "document.getElementById('sink').contentWindow;" +
            "document.getElementById('up').addEventListener('change', function () { document.getElementById('f').submit(); });");
        page.Settle();

        Assert.True(page.Session.SetFilesByUser(0, [new ChosenFile("a b.txt", "text/plain", DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), "hello"u8.ToArray())]));
        page.Settle();

        var sent = server.Single("/result");
        var boundary = sent.Header("Content-Type")!.Split("boundary=")[1];
        Assert.Equal(
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"q\"\r\n\r\nv\r\n" +
            $"--{boundary}\r\nContent-Disposition: form-data; name=\"up\"; filename=\"a b.txt\"\r\nContent-Type: text/plain\r\n\r\nhello\r\n" +
            $"--{boundary}--\r\n",
            sent.Body);
    }
}
