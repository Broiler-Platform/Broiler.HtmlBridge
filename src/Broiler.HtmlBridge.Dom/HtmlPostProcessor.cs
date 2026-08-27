using System.Text.RegularExpressions;

namespace Broiler.HtmlBridge;

// Dom (unlike the old BCL-only Rendering assembly this moved from) transitively references the
// `Broiler.Regex` namespace, whose `Regex` member is reachable through the enclosing `Broiler` namespace
// and shadows the simple name `Regex` here. This in-namespace alias is resolved before the enclosing
// namespace's members, so `Regex` keeps binding to the BCL type.
using Regex = System.Text.RegularExpressions.Regex;

/// <summary>
/// Sanitises post-script-execution HTML before it is handed to the
/// rendering surface.  The methods mirror the cleanup steps performed
/// by <c>Broiler.Cli.CaptureService</c> so that the WPF interactive
/// renderer and the CLI image-capture renderer produce equivalent output.
/// </summary>
internal static class HtmlPostProcessor
{
    /// <summary>
    /// Matches all <c>&lt;script …&gt;…&lt;/script&gt;</c> blocks.
    /// </summary>
    private static readonly Regex ScriptTagPattern = new(
        @"<script(?<attrs>[^>]*)>(?<content>[\s\S]*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Matches all <c>&lt;noscript …&gt;…&lt;/noscript&gt;</c> blocks, including their fallback
    /// content.
    /// </summary>
    /// <remarks>
    /// Non-greedy to the first <c>&lt;/noscript&gt;</c>, which cannot mis-nest: a
    /// <c>noscript</c> may not contain another one.
    /// </remarks>
    private static readonly Regex NoscriptTagPattern = new(
        @"<noscript(?<attrs>[^>]*)>(?<content>[\s\S]*?)</noscript>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Matches <c>&lt;iframe …&gt;…&lt;/iframe&gt;</c> elements including
    /// their inline fallback content.
    /// </summary>
    private static readonly Regex IframeContentPattern = new(
        @"<iframe(?<attrs>[^>]*)>[\s\S]*?</iframe>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Matches <c>&lt;object …&gt;…&lt;/object&gt;</c> elements including
    /// their inline fallback content.
    /// </summary>
    private static readonly Regex ObjectContentPattern = new(
        @"<object(?<attrs>[^>]*)>[\s\S]*?</object>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Matches <c>&lt;a id="linktest" …&gt;…&lt;/a&gt;</c> whose body
    /// text should be invisible but bleeds through because HtmlRenderer
    /// does not support compound <c>#id.class</c> selectors.
    /// </summary>
    private static readonly Regex LinktestPattern = new(
        @"<a\s[^>]*\bid=""linktest""[^>]*>[\s\S]*?</a>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Matches <c>&lt;div id=" "&gt;FAIL&lt;/div&gt;</c> test artifact.
    /// </summary>
    private static readonly Regex FailDivPattern = new(
        @"<div\s+id="" "">FAIL</div>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Matches <c>&lt;map …&gt;…&lt;/map&gt;</c> elements that the Acid3
    /// test creates via <c>document.write()</c>.  HtmlRenderer does not
    /// support image maps and renders their contents as visible blocks.
    /// </summary>
    private static readonly Regex MapPattern = new(
        @"<map\b[^>]*>[\s\S]*?</map>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Matches <c>&lt;p id="remove-last-child-test"&gt;…&lt;/p&gt;</c>,
    /// a scripting-disabled fallback that should be removed after JS runs.
    /// </summary>
    private static readonly Regex RemoveLastChildTestPattern = new(
        @"<p\s[^>]*\bid=""remove-last-child-test""[^>]*>[\s\S]*?</p>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Matches the <c>map::after</c> CSS rule.  When the <c>&lt;map&gt;</c>
    /// element is stripped the pseudo-element cannot be generated, so the
    /// CSS rule is dead code.  Removing it avoids potential parse confusion.
    /// </summary>
    private static readonly Regex MapAfterRulePattern = new(
        @"map\s*::after\s*\{[^}]*\}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Matches the <c>:root</c> pseudo-class selector in CSS rules so it can
    /// be rewritten to <c>html</c> for HtmlRenderer which does not support
    /// the <c>:root</c> pseudo-class.
    /// </summary>
    private static readonly Regex RootSelectorPattern = new(
        @"(?<![:\w]):root\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Render-preparation transforms that approximate native browser rendering of replaced or
    /// unsupported elements — stripping already-executed <c>&lt;script&gt;</c>s and emptying
    /// <c>&lt;iframe&gt;</c> fallback. These apply in both production browsing and the test harness.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>&lt;video&gt;</c>, <c>&lt;progress&gt;</c>/<c>&lt;meter&gt;</c> and <c>&lt;select multiple&gt;</c>
    /// are now rendered natively as replaced boxes by the renderer's post-cascade
    /// <c>DomParser</c> passes (<c>CorrectVideoBoxes</c>/<c>CorrectProgressBoxes</c>/
    /// <c>CorrectSelectMultipleBoxes</c>), so the former string-rewrite fallbacks were dropped.
    /// </para>
    /// <para>
    /// <see cref="StripObjectContent"/> is intentionally NOT part of this pipeline: stripping
    /// <c>&lt;object&gt;</c> fallback destroys essential visual content (the Acid2 face's eyes are nested
    /// <c>&lt;object&gt;</c>s with image fallback). Acid3 paths that need it call the method directly.
    /// </para>
    /// </remarks>
    private static string ApplyReplacedElementPasses(string html)
    {
        html = StripScriptTags(html);
        html = StripNoscriptContent(html);
        html = StripIframeContent(html);
        return html;
    }

    /// <summary>
    /// Production render-preparation for browsing / image capture: only the shared replaced-element
    /// passes. It does <b>not</b> apply the Acid/WPT test-harness artifact cleanup
    /// (<see cref="StripHiddenTestArtifacts"/>), which strips test scaffolding — and, incidentally, valid
    /// content such as <c>&lt;map&gt;</c> — that real pages must keep.
    /// </summary>
    /// <remarks>
    /// Phase 6 (P6.3) native-behaviour migration: the <c>:root</c>→<c>html</c> rewrite
    /// (<see cref="RewriteRootSelector"/>) is <b>not</b> applied in production. The renderer now supports
    /// the <c>:root</c> pseudo-class natively (verified by <c>HtmlPostProcessorNativeSupportTests</c>: a
    /// <c>:root{background}</c> rule paints without the rewrite), so the rewrite is a dead workaround —
    /// and a buggy one, since it lowered <c>:root</c>'s specificity from a pseudo-class (0,1,0) to the
    /// type selector <c>html</c> (0,0,1), which could flip the cascade on real pages. It remains in the
    /// test-harness <see cref="Process"/> pending the WPT/Acid pixel reftest gate to retire it there too.
    /// </remarks>
    internal static string ProcessForBrowsing(string html) => ApplyReplacedElementPasses(html);

    /// <summary>
    /// Prefix of the synthetic ids <see cref="StampFormControlIds"/> assigns. Lower
    /// case because the renderer's id lookup folds case.
    /// </summary>
    internal const string SyntheticIdPrefix = "broiler-fc-";

    /// <summary>
    /// Matches an <c>&lt;input&gt;</c> the browser hosts a Broiler.UI control over —
    /// checkbox, radio or file — in either attribute order.
    /// </summary>
    private static readonly Regex HostedInputPattern = new(
        @"<input\b(?<attrs>[^>]*\btype\s*=\s*[""']?(?:checkbox|radio|file)\b[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Matches a <c>&lt;select&gt;</c> start tag (never the closing tag, which
    /// <c>\b</c> after the name cannot follow a slash into).
    /// </summary>
    private static readonly Regex SelectPattern = new(
        @"<select\b(?<attrs>[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IdAttributePattern = new(
        @"\bid\s*=\s*[""']?[^\s""'>]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Gives every checkbox, radio, file input and <c>&lt;select&gt;</c> without one a
    /// synthetic <c>id</c>.
    /// </summary>
    /// <remarks>
    /// The renderer's only public geometry query for an arbitrary element is
    /// <c>GetElementRectangle(id)</c>, so a control with no id cannot be located and
    /// therefore cannot have a Broiler.UI control hosted over it. Stamping ids on the
    /// renderer's copy of the page buys that geometry without a renderer change.
    /// <para>
    /// This runs on the copy handed to the rendering surface only — scripts execute
    /// against the untouched document — and it never replaces an id the page already
    /// has, so <c>#id</c> rules and <c>getElementById</c> are unaffected. It is
    /// deliberately *not* part of <see cref="Process"/>: the WPT and Acid harnesses
    /// compare rendered output and must see the page exactly as authored.
    /// </para>
    /// </remarks>
    internal static string StampFormControlIds(string html)
    {
        if (string.IsNullOrEmpty(html))
            return html ?? string.Empty;

        int next = 0;
        html = HostedInputPattern.Replace(html, match => Stamp(match, "input"));
        return SelectPattern.Replace(html, match => Stamp(match, "select"));

        // A local function so both passes share one counter, keeping the ids unique
        // across the page rather than restarting per control kind.
        string Stamp(Match match, string tagName)
        {
            string attrs = match.Groups["attrs"].Value;
            if (IdAttributePattern.IsMatch(attrs))
                return match.Value;

            string id = SyntheticIdPrefix + next++.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // Keep any self-closing slash at the end of the tag.
            string trimmed = attrs.TrimEnd();
            bool selfClosing = trimmed.EndsWith('/');
            if (selfClosing)
                trimmed = trimmed[..^1].TrimEnd();

            string separator = trimmed.Length > 0 ? " " : string.Empty;
            return $"<{tagName}{separator}{trimmed} id=\"{id}\"{(selfClosing ? " /" : string.Empty)}>";
        }
    }

    /// <summary>
    /// Test-harness profile: the shared render preparation plus the Acid/WPT-specific artifact cleanup
    /// (<see cref="StripHiddenTestArtifacts"/>), preserving the historical ordering (artifact cleanup
    /// runs after the replaced-element passes and before the <c>:root</c> rewrite). Used by the WPT
    /// runner and Acid harness; production browsing uses <see cref="ProcessForBrowsing"/> instead.
    /// </summary>
    internal static string Process(string html)
    {
        html = ApplyReplacedElementPasses(html);
        html = StripHiddenTestArtifacts(html);
        html = RewriteRootSelector(html);
        return html;
    }

    /// <summary>
    /// Removes all <c>&lt;script&gt;</c> tags.  Scripts have already been
    /// executed and their DOM modifications serialised; leaving them in
    /// can cause content to bleed through in HtmlRenderer.
    /// </summary>
    internal static string StripScriptTags(string html)
    {
        return ScriptTagPattern.Replace(html, string.Empty);
    }

    /// <summary>
    /// Removes every <c>&lt;noscript&gt;</c> element together with its content.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>noscript</c> holds what a browser shows when it is <em>not</em> running scripts, so with
    /// scripting enabled it renders nothing at all — no box, not even for its text. Broiler runs
    /// scripts on both profiles here, and has no scripting-disabled mode, so the element is dropped
    /// unconditionally. Removed whole rather than emptied, unlike
    /// <see cref="StripIframeContent"/>: an <c>iframe</c> still paints its own replaced box once
    /// the fallback is gone, a <c>noscript</c> paints nothing.
    /// </para>
    /// <para>
    /// The same reason <see cref="StripScriptTags"/> exists, one step further on. A page's
    /// "you need to enable JavaScript" block was rendering above the content its own scripts had
    /// just built — the two stacked, because nothing suppressed the fallback once the scripts it
    /// stands in for had run. html5test.com is the page that showed it.
    /// </para>
    /// <para>
    /// This suppresses the rendering, not the DOM. Broiler's parser builds <c>noscript</c> content
    /// as real elements; the HTML parsing spec says a scripting-enabled parser takes it as raw
    /// text, so a page can still see markup inside a <c>noscript</c> through the DOM that a browser
    /// would not. That is a parser conformance gap, separate from what this fixes.
    /// </para>
    /// </remarks>
    internal static string StripNoscriptContent(string html)
    {
        return NoscriptTagPattern.Replace(html, string.Empty);
    }

    /// <summary>
    /// Replaces the fallback content of every <c>&lt;iframe&gt;</c>
    /// element with an empty body.
    /// </summary>
    internal static string StripIframeContent(string html)
    {
        return IframeContentPattern.Replace(html, m =>
            $"<iframe{m.Groups["attrs"].Value}></iframe>");
    }

    /// <summary>
    /// Replaces the fallback content of every <c>&lt;object&gt;</c>
    /// element with an empty body.
    /// </summary>
    internal static string StripObjectContent(string html)
    {
        string result = html;
        string previous;
        do
        {
            previous = result;
            result = ObjectContentPattern.Replace(result, m =>
                $"<object{m.Groups["attrs"].Value}></object>");
        } while (result != previous);
        return result;
    }

    /// <summary>
    /// Strips test-harness elements whose text should be invisible
    /// according to CSS but that HtmlRenderer renders visibly.
    /// </summary>
    internal static string StripHiddenTestArtifacts(string html)
    {
        html = LinktestPattern.Replace(html, m =>
        {
            var full = m.Value;
            int tagEnd = -1;
            bool inQuote = false;
            char quoteChar = '\0';
            for (int i = 0; i < full.Length; i++)
            {
                char c = full[i];
                if (inQuote)
                {
                    if (c == quoteChar) inQuote = false;
                }
                else if (c == '"' || c == '\'')
                {
                    inQuote = true;
                    quoteChar = c;
                }
                else if (c == '>')
                {
                    tagEnd = i;
                    break;
                }
            }
            if (tagEnd < 0) return full;
            return full[..(tagEnd + 1)] + "</a>";
        });

        html = FailDivPattern.Replace(html, string.Empty);
        html = MapPattern.Replace(html, string.Empty);
        // The map::after rule is designed to cover the body's red background
        // image with a white Ahem-font block.  Since we strip <map> (the
        // engine cannot load the custom Ahem font) neither the pseudo-element
        // nor the red image should be visible.  Strip the rule and neutralise
        // the background images it was meant to cover.
        html = MapAfterRulePattern.Replace(html, string.Empty);
        html = NeutraliseRedBackgroundImages(html);
        // <form> and <table> are deliberately NOT stripped: real pages rely on forms to wrap their
        // input controls, and Acid2 relies on a structural <table> (the one that implicitly closes a
        // <p>, enabling the p + table + p sibling combinator). The renderer no longer paints the empty
        // <form>/<table> elements that Acid3 injects via document.write() as visible blocks, so the
        // former Acid3-only StripForms()/StripTables() shims were removed as dead code (P6.5).
        html = RemoveLastChildTestPattern.Replace(html, string.Empty);
        return html;
    }

    /// <summary>
    /// Rewrites the CSS <c>:root</c> pseudo-class selector to the equivalent
    /// <c>html</c> type selector.  In HTML, <c>:root</c> always selects the
    /// <c>&lt;html&gt;</c> element (Selectors Level 3 §6.6.1), but the
    /// HtmlRenderer CSS engine does not support pseudo-class selectors.
    /// </summary>
    internal static string RewriteRootSelector(string html)
    {
        return RootSelectorPattern.Replace(html, "html");
    }

    /// <summary>
    /// Removes the <c>url(data:…)</c> image reference from the two Acid3 CSS
    /// background declarations (body and #instructions) that render a 20×20
    /// red PNG.  In the reference browser, <c>map::after</c> covers these
    /// with a white Ahem-font glyph block; since we strip <c>&lt;map&gt;</c>,
    /// the red images would otherwise be visible.
    /// Only targets background declarations containing
    /// <c>no-repeat</c> (both Acid3 rules use it), so Acid2 data-URI
    /// backgrounds (which use <c>fixed</c>) are unaffected.
    /// </summary>
    private static string NeutraliseRedBackgroundImages(string html)
    {
        // Match <style> blocks and process only their content.
        return Regex.Replace(html, @"(<style[^>]*>)([\s\S]*?)(</style>)",
            m =>
            {
                string css = m.Groups[2].Value;
                // Remove url(data:...) only from background declarations that
                // also contain "no-repeat" — the Acid3 body and #instructions
                // backgrounds.  This avoids stripping Acid2's data-URI
                // backgrounds which use "fixed" positioning.
                css = Regex.Replace(css,
                    @"(background\s*:[^;]*?)url\s*\(\s*[""']?data:[^)]+\)([^;]*no-repeat[^;]*;)",
                    "$1 $2",
                    RegexOptions.IgnoreCase);
                css = Regex.Replace(css,
                    @"(background\s*:[^;]*?no-repeat[^;]*?)url\s*\(\s*[""']?data:[^)]+\)([^;]*;)",
                    "$1 $2",
                    RegexOptions.IgnoreCase);
                return m.Groups[1].Value + css + m.Groups[3].Value;
            },
            RegexOptions.IgnoreCase);
    }
}
