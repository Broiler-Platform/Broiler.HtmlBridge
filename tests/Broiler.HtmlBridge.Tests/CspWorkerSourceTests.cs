using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Where a Content-Security-Policy lets a document start a worker from (CSP3 §6.1.2): <c>worker-src</c>,
/// falling back to <c>child-src</c>, then <c>script-src</c>, then <c>default-src</c>. The policy parser
/// ignored <c>worker-src</c> and <c>child-src</c>, and nothing asked a policy about a worker at all.
/// </summary>
public class CspWorkerSourceTests
{
    private const string Page = "https://example.test/page.html";
    private const string Own = "https://example.test/worker.js";
    private const string Other = "https://other.test/worker.js";

    [Theory]
    // The first directive stated decides, whatever the ones it would fall back to say.
    [InlineData("script-src 'none'; worker-src 'self'", Own, true)]
    [InlineData("script-src 'self'; worker-src 'none'", Own, false)]
    [InlineData("script-src 'self'; child-src https://other.test", Own, false)]
    [InlineData("script-src 'self'; child-src https://other.test", Other, true)]
    [InlineData("default-src 'none'; script-src 'self'", Own, true)]
    [InlineData("default-src 'self'", Own, true)]
    [InlineData("default-src 'self'", Other, false)]
    // No policy that speaks about scripts leaves workers alone.
    [InlineData("style-src 'none'", Other, true)]
    // A worker is not parser-inserted, so 'strict-dynamic' in the script directive it falls back to
    // admits it -- unless worker-src itself is stated.
    [InlineData("script-src 'nonce-abc' 'strict-dynamic'", Other, true)]
    [InlineData("script-src 'nonce-abc' 'strict-dynamic'; worker-src 'self'", Other, false)]
    // `*` does not admit a local scheme; naming the scheme does.
    [InlineData("worker-src *", "data:text/javascript,1", false)]
    [InlineData("worker-src data:", "data:text/javascript,1", true)]
    [InlineData("worker-src 'self' blob:", "blob:https://example.test/1", true)]
    public void TheFirstWorkerDirectiveStatedDecides(string policy, string workerUrl, bool allowed)
    {
        Assert.Equal(allowed, CspFixture.CspOf(policy).AllowsWorker(workerUrl, Page));
    }
}
