using Broiler.HtmlBridge.Net;

namespace Broiler.HtmlBridge.Tests;

public class NavigationProtocolTests
{
    [Theory]
    [InlineData("https://example.test/", "2.0", "h2")]
    [InlineData("https://example.test/", "1.1", "http/1.1")]
    [InlineData("https://example.test/", null, "")]
    [InlineData("file:///page.html", "2.0", "")]
    public void NavigationReportsOnlyTheObservedProtocol(string url, string? version, string expected)
    {
        var timing = DocumentFetchTiming.StartNavigation();
        timing.RecordResponseVersion(version is null ? null : Version.Parse(version));
        var engine = new ScriptEngine { DocumentFetchTiming = timing };
        var html = engine.Execute(
            ["document.getElementById('out').textContent = '[' + performance.getEntriesByType('navigation')[0].nextHopProtocol + ']';"],
            "<html><body><div id=\"out\"></div></body></html>", url);
        Assert.Contains($"<div id=\"out\">[{expected}]</div>", html);
    }
}
