using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A document asks the network for each stylesheet once: the sheet's other uses are answered with what
/// the first request got, as a browser's memory cache answers them, a failed request included.
/// </summary>
/// <remarks>
/// An <c>@import</c> is fetched each time the sheet holding it is read, which is each time the
/// document's style scope is assembled -- whenever its sheets change -- and once for every render
/// projection. Every one went to the network, on the thread that asked: reCAPTCHA's demo page imports
/// a font sheet, which was fetched about 3000 times while the page loaded and 17 times a second after.
/// The server counts the requests (<see cref="LoopbackStyleServer"/>).
/// </remarks>
public class StyleSheetFetchOnceTests
{
    private const string Blue = "rgb(0, 0, 255)";

    /// <summary>
    /// A linked sheet, what it imports and what a <c>&lt;style&gt;</c> imports are each requested once,
    /// through five changes of the document's sheets, each read by a style query, and three
    /// serializations -- and what they hold still applies.
    /// </summary>
    [Fact]
    public void EachSheetIsRequestedOnceADocument()
    {
        using var server = new LoopbackStyleServer(new Dictionary<string, string>
        {
            ["/main.css"] = "@import url(/fonts.css); #a { color: rgb(0, 128, 0) }",
            ["/fonts.css"] = "#b { color: rgb(0, 0, 255) }",
            ["/inline.css"] = "#c { color: rgb(0, 0, 255) }",
        });

        using var session = Start(
            server,
            "<link rel=\"stylesheet\" href=\"/main.css\"><style>@import url(/inline.css);</style>",
            "var log = [];" +
            "for (var i = 0; i < 5; i++) {" +
            " var sheet = document.createElement('style'); sheet.textContent = '#x' + i + ' { color: red }';" +
            " document.head.appendChild(sheet);" +
            " log.push(getComputedStyle(document.getElementById('b')).color + ' ' +" +
            "  getComputedStyle(document.getElementById('c')).color); }" +
            "document.getElementById('out').textContent = log.join('|');");

        for (var i = 0; i < 3; i++)
            session.CurrentHtml();

        Assert.Equal(string.Join("|", Enumerable.Repeat($"{Blue} {Blue}", 5)), PageProbe.OutOf(session.CurrentHtml(), decode: true));
        Assert.Equal(1, server.RequestCount("/main.css"));
        Assert.Equal(1, server.RequestCount("/fonts.css"));
        Assert.Equal(1, server.RequestCount("/inline.css"));
    }

    /// <summary>An import that failed is not asked for again when its sheet is read again.</summary>
    [Fact]
    public void AFailedImportIsNotRequestedAgain()
    {
        using var server = new LoopbackStyleServer(new Dictionary<string, string>());

        using var session = Start(
            server,
            "<style>@import url(/missing.css); #b { color: rgb(0, 0, 255) }</style>",
            "var log = [];" +
            "for (var i = 0; i < 3; i++) {" +
            " var sheet = document.createElement('style'); sheet.textContent = '#x' + i + ' { color: red }';" +
            " document.head.appendChild(sheet);" +
            " log.push(getComputedStyle(document.getElementById('b')).color); }" +
            "document.getElementById('out').textContent = log.join('|');");

        session.CurrentHtml();

        Assert.Equal($"{Blue}|{Blue}|{Blue}", PageProbe.OutOf(session.CurrentHtml(), decode: true));
        Assert.Equal(1, server.RequestCount("/missing.css"));
    }

    private static InteractiveSession Start(LoopbackStyleServer server, string head, string script)
    {
        var session = new ScriptEngine().ExecuteInteractive(
            [script], [],
            $"<!DOCTYPE html><html><head>{head}</head>" +
            "<body><p id=\"a\">a</p><p id=\"b\">b</p><p id=\"c\">c</p><div id=\"out\"></div></body></html>",
            server.PageUrl);
        Assert.NotNull(session);
        session!.SettleLoadWindow();
        return session;
    }
}
