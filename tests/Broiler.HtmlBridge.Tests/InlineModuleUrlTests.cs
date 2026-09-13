using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Scripting;

namespace Broiler.Browser.Core.Tests;

/// <summary>
/// What a module reports as its own <c>import.meta.url</c>, and what its relative specifiers resolve
/// against — the two facts that had silently diverged for an inline root.
/// </summary>
/// <remarks>
/// <para>
/// <b>ES modules had no test in this suite at all before this file.</b> Nothing loaded a
/// <c>&lt;script type="module"&gt;</c>, which is why an inline root reporting <c>inline:0</c> as its
/// URL survived: it is the value a page reads, and no page was reading it.
/// </para>
/// <para>
/// <b>Every assertion here is on the exact string, never on "not null".</b> The defect produced a
/// value that is non-null, is a syntactically valid absolute URI, and round-trips through
/// <see cref="Uri"/> unchanged — <c>Uri.TryCreate("inline:0", UriKind.Absolute, out _)</c> is
/// <see langword="true"/>, because <c>inline</c> parses as a scheme. A null check, a
/// <c>TryCreate</c> check and an <c>IsAbsoluteUri</c> check all pass on the wrong answer.
/// </para>
/// </remarks>
public class InlineModuleUrlTests
{
    private const string PageUrl = "https://example.test/dir/page.html";

    private static string Run(string moduleBody)
    {
        var html = $$"""
            <!DOCTYPE html><html><body>
            <div id="out">unset</div>
            <script type="module">
            {{moduleBody}}
            </script>
            </body></html>
            """;

        // Extracted rather than hand-built, so the test exercises the real key and base the document
        // produces (Core/Scripting/ScriptExtractionService.cs) instead of a reconstruction of them.
        var extraction = ScriptExtractionService.ExtractAll(html, PageUrl);
        Assert.Single(extraction.ModuleRoots);

        var rendered = new ScriptEngine().Execute([], [], html, PageUrl, extraction.ModuleRoots);
        Assert.NotNull(rendered);

        const string open = "<div id=\"out\">";
        var start = rendered!.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, "no #out div in the serialized output");
        start += open.Length;
        var end = rendered.IndexOf("</div>", start, StringComparison.Ordinal);
        Assert.True(end >= 0, "unterminated #out div");
        return rendered[start..end];
    }

    [Fact]
    public void AnInlineModuleReportsTheDocumentUrlAndNotItsSyntheticKey()
    {
        // The whole claim. The extractor keys an inline root `inline:<document order>` so that two
        // inline modules on one page do not dedup onto each other, and that key is NOT a URL — but
        // it parses as one, so it was reported verbatim.
        Assert.Equal(
            $"meta={PageUrl}",
            Run("document.getElementById('out').textContent = 'meta=' + import.meta.url;"));
    }

    [Fact]
    public void TheSecondInlineModuleOnAPageReportsTheSameDocumentUrlAsTheFirst()
    {
        // THE CONTROL FOR THE FIX OVERREACHING. Keys stay per-occurrence unique, so a fix that made
        // the key equal the document URL would collapse two inline roots into one module-map entry
        // and the second would never run. Both must run AND both must report the document's URL.
        var html = $$"""
            <!DOCTYPE html><html><body>
            <div id="out">unset</div>
            <script type="module">globalThis.__first = import.meta.url;</script>
            <script type="module">
              document.getElementById('out').textContent =
                'first=' + globalThis.__first + ' second=' + import.meta.url;
            </script>
            </body></html>
            """;

        var extraction = ScriptExtractionService.ExtractAll(html, PageUrl);

        // Two roots, two distinct keys, one shared base: the reason the key cannot simply become the
        // URL, stated as an assertion rather than in a comment.
        Assert.Equal(2, extraction.ModuleRoots.Count);
        Assert.Equal(
            ["inline:0", "inline:1"],
            extraction.ModuleRoots.Select(r => r.Key).ToArray());
        Assert.Equal(
            [PageUrl, PageUrl],
            extraction.ModuleRoots.Select(r => r.BaseUrl).ToArray());

        var rendered = new ScriptEngine().Execute([], [], html, PageUrl, extraction.ModuleRoots);
        Assert.NotNull(rendered);

        const string open = "<div id=\"out\">";
        var start = rendered!.IndexOf(open, StringComparison.Ordinal) + open.Length;
        var end = rendered.IndexOf("</div>", start, StringComparison.Ordinal);

        Assert.Equal($"first={PageUrl} second={PageUrl}", rendered[start..end]);
    }

    [Fact]
    public void AnImportedModuleStillReportsItsOwnUrlAndNotTheImportersUrl()
    {
        // THE OTHER CONTROL. The fix maps ROOT keys only. A module reached by import has its own key,
        // which already is its URL, and must be untouched — a fix that answered the page URL for
        // every module would pass the first test and break this one.
        Assert.Equal(
            "imported=data:text/javascript,export%20const%20u%20=%20import.meta.url;",
            Run("""
                import { u } from 'data:text/javascript,export const u = import.meta.url;';
                document.getElementById('out').textContent = 'imported=' + u;
                """));
    }

    [Fact]
    public void ARelativeSpecifierFromAnInlineModuleStillResolvesAgainstTheDocument()
    {
        // THE REGRESSION GUARD, and the assertion that shows the two facts were always separate.
        // Resolution reads the base handed to RunScriptAsync, not the key, so this answered correctly
        // even while import.meta.url did not. The rejection text carries the resolved URL, so it
        // distinguishes "resolved against the document and could not be fetched" from "did not
        // resolve at all" — which is what a fix that confused base and key would produce.
        Assert.Equal(
            "dyn=REJECTED: module not found: https://example.test/dir/sibling.js",
            Run("""
                var out = document.getElementById('out');
                import('./sibling.js').then(
                  function () { out.textContent = 'dyn=RESOLVED-AND-LOADED'; },
                  function (e) { out.textContent = 'dyn=REJECTED: ' + (e && e.message ? e.message : String(e)); });
                """));
    }
}
