using System.Text;

using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Scripting;

using TextRegex = System.Text.RegularExpressions.Regex;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Fixture plumbing shared by the Content-Security-Policy and stylesheet-import suites: building a
/// policy, putting one in a <c>&lt;meta&gt;</c>, and reading a stylesheet back out of the render
/// projection.
/// </summary>
internal static class CspFixture
{
    /// <summary>
    /// The policy <paramref name="text"/> describes, or <see langword="null"/> for no policy at all —
    /// which is a different thing from an empty policy and is what the "no CSP" cases pass.
    /// </summary>
    internal static ContentSecurityPolicy? Csp(string? text)
    {
        if (text is null)
            return null;

        var policy = new ContentSecurityPolicy();
        policy.Parse(text);
        return policy;
    }

    /// <summary>
    /// <see cref="Csp"/> for a caller that always has a policy, so the result needs no null check.
    /// </summary>
    internal static ContentSecurityPolicy CspOf(string text) => Csp(text)!;

    /// <summary>The <c>&lt;meta&gt;</c> element that delivers <paramref name="policy"/> to a document.</summary>
    internal static string Meta(string policy, string httpEquiv = "Content-Security-Policy") =>
        $"<meta http-equiv=\"{httpEquiv}\" content=\"{policy}\">";

    /// <summary>
    /// A page whose head is nothing but <paramref name="policy"/>'s <c>&lt;meta&gt;</c>, or nothing at
    /// all when it is <see langword="null"/>.
    /// </summary>
    internal static string MetaPage(string? policy, string body) =>
        "<html><head>" + (policy is null ? string.Empty : Meta(policy)) + "</head><body>" + body + "</body></html>";

    /// <summary>
    /// A stylesheet as a <c>data:</c> URL. Base64 rather than percent-encoding because a raw <c>)</c>
    /// from an <c>rgb(...)</c> inside a <c>url(...)</c> turns the test into a parsing question rather
    /// than the policy question it is meant to be.
    /// </summary>
    internal static string DataUrl(string css) =>
        "data:text/css;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(css));

    /// <summary>
    /// Renders <paramref name="pageHtml"/> under an optional <paramref name="header"/> policy and
    /// returns the projection's HTML. The one script is <c>1;</c> because <c>Execute</c> answers
    /// <see langword="null"/> when handed none.
    /// </summary>
    internal static string RunProjection(
        string pageHtml, string? pageUrl, string script = "1;", ContentSecurityPolicy? header = null)
    {
        var engine = header is null ? new ScriptEngine() : new ScriptEngine { Csp = header };
        var html = engine.Execute([script], pageHtml, pageUrl);
        Assert.NotNull(html);
        return html!;
    }

    /// <summary>
    /// The projected text of the <c>&lt;style&gt;</c> with <paramref name="id"/>.
    /// <para>
    /// Matches the opening tag by regex, so it tolerates the other attributes the import tests put
    /// on a <c>&lt;style&gt;</c>. <see cref="PageProbe.StyleTextOf"/> is the stricter exact-tag read;
    /// the two are not interchangeable and collapsing them would change what the import suite accepts.
    /// </para>
    /// </summary>
    internal static string StyleText(string html, string id = "s")
    {
        var match = TextRegex.Match(html, $"<style[^>]*\\bid=\"{id}\"[^>]*>([\\s\\S]*?)</style>");
        Assert.True(match.Success, $"no <style id=\"{id}\"> in the projection: {html}");
        return match.Groups[1].Value;
    }
}
