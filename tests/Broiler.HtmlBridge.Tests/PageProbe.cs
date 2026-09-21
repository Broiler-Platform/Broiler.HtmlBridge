using System.Net;

using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The <c>#out</c> page probe the suite reads its answers through: script writes a value into a
/// <c>&lt;div id="out"&gt;</c> in the fixture document, and the test reads it back out of the
/// serialized DOM.
/// <para>
/// Going through the serialized page keeps a test on the engine's public surface. The bindings
/// under test are <c>internal</c>, and reaching for one directly would pin its shape rather than
/// its behaviour.
/// </para>
/// </summary>
internal static class PageProbe
{
    /// <summary>The script that writes <paramref name="expression"/>'s value into <c>#out</c>.</summary>
    internal static string Probe(string expression) =>
        $"document.getElementById('out').textContent = String({expression});";

    /// <summary>
    /// The same probe with a throw recorded rather than propagated: <c>#out</c> receives
    /// <c>threw &lt;name&gt;: &lt;message&gt;</c>, so a test can assert on the failure itself.
    /// </summary>
    internal static string GuardedProbe(string expression) =>
        "var probeResult;" +
        $"try {{ probeResult = String({expression}); }} " +
        "catch (e) { probeResult = 'threw ' + (e && e.name) + ': ' + (e && e.message); }" +
        "document.getElementById('out').textContent = probeResult;";

    /// <summary>
    /// Runs <paramref name="scripts"/> against the fixture and returns the serialized page,
    /// asserting only that the engine produced one.
    /// </summary>
    internal static string Render(IReadOnlyList<string> scripts, string pageHtml, string? pageUrl)
    {
        var html = new ScriptEngine().Execute(scripts, pageHtml, pageUrl);

        Assert.NotNull(html);

        return html!;
    }

    /// <summary>
    /// What the page wrote to <c>#out</c>, read out of <paramref name="html"/>. Pass
    /// <paramref name="decode"/> when the asserted value can contain a character the serializer
    /// writes as an entity — a CSS string's quotes, most often.
    /// </summary>
    internal static string OutOf(string html, bool decode = false)
    {
        const string open = "<div id=\"out\">";
        var start = html.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no #out div in serialized output: {html}");
        start += open.Length;
        var end = html.IndexOf("</div>", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"unterminated #out div in serialized output: {html}");
        var text = html[start..end];
        return decode ? WebUtility.HtmlDecode(text) : text;
    }

    /// <summary>
    /// Runs <paramref name="script"/> against the fixture page and returns what it wrote to
    /// <c>#out</c>. A throw propagates, which is what the test wants when the script is not the
    /// thing under test.
    /// </summary>
    internal static string RunAgainst(string pageHtml, string? pageUrl, string script, bool decode = false) =>
        OutOf(Render([Probe(script)], pageHtml, pageUrl), decode);

    /// <summary>
    /// As <see cref="RunAgainst"/>, but with <paramref name="helpers"/> evaluated first and a throw
    /// written to <c>#out</c> rather than propagated.
    /// </summary>
    internal static string RunGuarded(
        string pageHtml, string? pageUrl, string helpers, string script, bool decode = false) =>
        OutOf(Render([helpers, GuardedProbe(script)], pageHtml, pageUrl), decode);

    /// <summary>
    /// The text of the <c>&lt;style&gt;</c> with <paramref name="id"/> in the serialized page.
    /// <para>
    /// Deliberately not <see cref="CspFixture.StyleText"/>, which matches the opening tag by regex
    /// and so tolerates attributes beside the <c>id</c>. This one requires the tag to be exactly
    /// <c>&lt;style id="..."&gt;</c>, which is the stricter read the CSSOM write-through tests want.
    /// </para>
    /// </summary>
    internal static string StyleTextOf(string html, string id)
    {
        var open = $"<style id=\"{id}\">";
        var start = html.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no <style id=\"{id}\"> in serialized output: {html}");
        start += open.Length;
        var end = html.IndexOf("</style>", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"unterminated <style id=\"{id}\"> in serialized output: {html}");
        return html[start..end];
    }
}
