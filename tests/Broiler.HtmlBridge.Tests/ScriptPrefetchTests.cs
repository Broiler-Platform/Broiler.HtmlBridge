using Broiler.HtmlBridge.Scripting;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The script side of concurrent sub-resource fetching: which scripts a document prefetches, and
/// that prefetching changes nothing about what the extractor returns or the order it returns it in.
/// The prefetcher itself is <c>SubResourcePrefetcherTests</c>'s.
/// </summary>
public sealed class ScriptPrefetchTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        "broiler-script-prefetch-" + Guid.NewGuid().ToString("N"));

    public ScriptPrefetchTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private void WriteScript(string name, string body) => File.WriteAllText(Path.Combine(_dir, name), body);

    private string PageUrl => new Uri(Path.Combine(_dir, "page.html")).AbsoluteUri;

    [Fact]
    public void EveryExternalScriptIsPrefetched()
    {
        const string html = """
            <script src="a.js"></script>
            <script src="b.js"></script>
            <script src="c.js"></script>
            """;

        var prefetcher = ScriptExtractionService.CreateScriptPrefetcher(html, PageUrl, ContentSecurityPolicySet.None);

        Assert.NotNull(prefetcher);
        Assert.Equal(3, prefetcher!.RequestedCount);
    }

    [Fact]
    public void AScriptIncludedTwiceIsRequestedOnce()
    {
        const string html = """
            <script src="a.js"></script>
            <script src="a.js"></script>
            <script src="b.js"></script>
            """;

        var prefetcher = ScriptExtractionService.CreateScriptPrefetcher(html, PageUrl, ContentSecurityPolicySet.None);

        Assert.Equal(2, prefetcher!.RequestedCount);
    }

    [Fact]
    public void InlineAndDataUrlScriptsAreNotRequested()
    {
        const string html = """
            <script>var inline = 1;</script>
            <script src="data:text/javascript,var d = 2;"></script>
            <script src="a.js"></script>
            <script src="b.js"></script>
            """;

        var prefetcher = ScriptExtractionService.CreateScriptPrefetcher(html, PageUrl, ContentSecurityPolicySet.None);

        Assert.Equal(2, prefetcher!.RequestedCount);
    }

    [Fact]
    public void ADocumentWithOneExternalScriptGetsNoPrefetcher()
    {
        // Nothing to overlap a single round trip with, so the sequential path is left exactly as
        // it was rather than paying for machinery that cannot help.
        const string html = """<script src="only.js"></script>""";

        Assert.Null(ScriptExtractionService.CreateScriptPrefetcher(html, PageUrl, ContentSecurityPolicySet.None));
    }

    [Fact]
    public void AScriptThePolicyBlocksIsNotRequested()
    {
        // Prefetching a blocked script would put the very request the policy forbids on the wire:
        // "fetched but not executed" is not what the policy says. Three scripts are named and two
        // are requested, so the blocked one is the one left out.
        const string html = """
            <meta http-equiv="Content-Security-Policy" content="script-src 'self'">
            <script src="https://evil.example/x.js"></script>
            <script src="a.js"></script>
            <script src="b.js"></script>
            """;
        var csp = ContentSecurityPolicy.FromHtml(html);
        Assert.NotNull(csp);

        var prefetcher = ScriptExtractionService.CreateScriptPrefetcher(html, PageUrl, new ContentSecurityPolicySet(csp));

        Assert.Equal(2, prefetcher!.RequestedCount);
    }

    [Fact]
    public void ExtractionReturnsTheSameScriptsInTheSameOrderAsWithoutPrefetching()
    {
        // Prefetching may only change when a request starts, never what the document ends up
        // executing or in what order.
        WriteScript("a.js", "var a = 1;");
        WriteScript("b.js", "var b = 2;");
        WriteScript("c.js", "var c = 3;");
        const string html = """
            <script src="a.js"></script>
            <script>var inline = 0;</script>
            <script src="b.js"></script>
            <script src="c.js" defer></script>
            """;

        var result = ScriptExtractionService.ExtractAll(html, PageUrl);

        Assert.Equal(["var a = 1;", "var inline = 0;", "var b = 2;"], result.Scripts);
        Assert.Equal(["var c = 3;"], result.DeferredScripts);
    }

    [Fact]
    public void AScriptThatCannotBeFetchedIsSkipped()
    {
        WriteScript("a.js", "var a = 1;");
        const string html = """
            <script src="a.js"></script>
            <script src="missing.js"></script>
            <script src="also-missing.js"></script>
            """;

        var result = ScriptExtractionService.ExtractAll(html, PageUrl);

        Assert.Equal(["var a = 1;"], result.Scripts);
    }
}
