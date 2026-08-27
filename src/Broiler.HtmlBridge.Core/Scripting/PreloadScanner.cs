using Broiler.Dom.Html;
using Broiler.HtmlBridge.Internal.Scripting;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Broiler.HtmlBridge;

/// <summary>The family of sub-resource a <see cref="PreloadCandidate"/> belongs to.</summary>
/// <remarks>
/// The families are the ones with a distinct <em>consume</em> site in the bridge, because a prefetch
/// is only useful if the site that later blocks on the resource looks it up under the same key. They
/// are not a taxonomy of HTML elements.
/// </remarks>
public enum PreloadKind
{
    /// <summary>An external classic or module script (<c>&lt;script src&gt;</c>, <c>&lt;link rel=modulepreload&gt;</c>).</summary>
    Script,

    /// <summary>An external stylesheet (<c>&lt;link rel=stylesheet&gt;</c>).</summary>
    StyleSheet,

    /// <summary>An image (<c>&lt;img src&gt;</c>, <c>&lt;input type=image src&gt;</c>).</summary>
    Image,

    /// <summary>A sub-document or plugin resource (<c>&lt;iframe&gt;</c>, <c>&lt;frame&gt;</c>, <c>&lt;object data&gt;</c>, <c>&lt;embed src&gt;</c>).</summary>
    Frame,
}

/// <summary>
/// One sub-resource the document names, in both of the forms a consume site may key it by.
/// </summary>
/// <param name="Kind">Which family the resource belongs to.</param>
/// <param name="RawUrl">
/// The attribute value exactly as written. Some consume sites key by this — the stylesheet path
/// passes the raw <c>href</c> straight to the loader — and a prefetch stored under a differently
/// normalized key is never consumed, so it doubles the requests instead of overlapping them.
/// </param>
/// <param name="ResolvedUrl">
/// The absolute URL the raw one resolves to against the document base, or <c>null</c> when it
/// cannot be resolved (a relative URL in a document with no base). The script path keys by this.
/// </param>
/// <param name="Nonce">
/// The element's <c>nonce</c> attribute, carried so a Content Security Policy check made before the
/// DOM exists can reach the same verdict as the one the consume site makes with the element in hand.
/// Without it a policy of the form <c>script-src 'nonce-…'</c> would refuse every prefetch and the
/// scan would silently do nothing on exactly the pages that declare one.
/// </param>
public readonly record struct PreloadCandidate(
    PreloadKind Kind,
    string RawUrl,
    string? ResolvedUrl,
    string? Nonce = null);

/// <summary>
/// What one scan of a document found: its base URL and the sub-resources it names, in document
/// order.
/// </summary>
public sealed class PreloadScanResult
{
    internal static readonly PreloadScanResult Empty = new(null, []);

    internal PreloadScanResult(string? documentBaseUrl, IReadOnlyList<PreloadCandidate> candidates)
    {
        DocumentBaseUrl = documentBaseUrl;
        Candidates = candidates;
    }

    /// <summary>
    /// The URL relative references in this document resolve against — the first
    /// <c>&lt;base href&gt;</c> resolved against the page URL, or the page URL when there is none.
    /// </summary>
    public string? DocumentBaseUrl { get; }

    /// <summary>Every candidate found, in document order, deduplicated by kind and raw URL.</summary>
    public IReadOnlyList<PreloadCandidate> Candidates { get; }

    /// <summary>The raw attribute values of one kind, in document order.</summary>
    public IReadOnlyList<string> RawUrls(PreloadKind kind) =>
        [.. Candidates.Where(c => c.Kind == kind).Select(c => c.RawUrl)];

    /// <summary>
    /// The resolved absolute URLs of one kind, in document order. A candidate that could not be
    /// resolved is dropped rather than passed through unresolved: an unresolvable URL is one no
    /// fetch could have been issued for either.
    /// </summary>
    public IReadOnlyList<string> ResolvedUrls(PreloadKind kind) =>
        [.. Candidates.Where(c => c.Kind == kind && c.ResolvedUrl is not null).Select(c => c.ResolvedUrl!)];
}

/// <summary>
/// Finds every sub-resource URL an HTML document names, without building a DOM. Multithreading
/// roadmap item #17 — the speculative preload scan.
/// </summary>
/// <remarks>
/// <para>
/// The roadmap's shape for item #17 is "a worker scans raw bytes for <c>src</c>/<c>href</c> while
/// the main parse runs, feeding #2". This type is the scan; <see cref="SpeculativePreloadScan"/> is
/// the worker. It exists because the two remaining sub-resource families of item #2 — sub-documents
/// and <c>fetch()</c>/XHR — have no point at which their URL set is known before it is consumed: a
/// frame's URL becomes known when the element is reached, which is the moment it is loaded. A
/// speculative scan is the only thing that can produce a URL set earlier than the parse does, which
/// is why the roadmap calls it their <em>only</em> source of a prefetch trigger rather than an
/// amplifier.
/// </para>
/// <para>
/// <b>It tokenizes rather than pattern-matching bytes.</b> The roadmap says "scans raw bytes", and a
/// regex over the buffer is what that phrasing suggests, but <see cref="HtmlTokenizer"/> is already
/// available, is a pure function of an immutable string, and gets the cases a pattern gets wrong:
/// a <c>&lt;img src&gt;</c> inside a comment or inside a <c>&lt;script&gt;</c> body is not a
/// resource, a <c>&gt;</c> inside a quoted attribute does not end a tag, and attribute names are
/// matched from the parsed map rather than by position. Speculating a request the document never
/// makes is not free — it is a round trip charged to the origin — so the accuracy is worth the pass.
/// </para>
/// <para>
/// <b>What it deliberately does not find, and why.</b>
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b><c>srcset</c> and <c>&lt;picture&gt;</c>.</b> Which candidate a browser fetches depends on the
/// viewport and the device pixel ratio, neither of which a scan running before layout knows.
/// Preloading all of them would put <em>n</em> requests on the wire for one image, which is worse
/// than the serial fetch this is trying to remove.
/// </description></item>
/// <item><description>
/// <b>Anything inside <c>&lt;template&gt;</c>.</b> Template content is inert: its resources are not
/// fetched unless the fragment is cloned into the document, which is a script decision this scan
/// cannot predict.
/// </description></item>
/// <item><description>
/// <b>Anything inside <c>&lt;noscript&gt;</c>.</b> Broiler runs scripts, so the parser treats a
/// <c>noscript</c> body as text and nothing in it is ever loaded.
/// </description></item>
/// <item><description>
/// <b>Non-fetchable URL schemes</b> — <c>data:</c>, <c>about:</c>, <c>javascript:</c>,
/// <c>blob:</c> — and bare fragments. There is no round trip to overlap.
/// </description></item>
/// <item><description>
/// <b>CSS <c>url()</c> references.</b> They are inside a stylesheet this scan has not fetched yet;
/// discovering them is a second scan of the sheet, which belongs with whoever consumes it.
/// </description></item>
/// </list>
/// <para>
/// <b>Base resolution happens after the pass, not during it.</b> HTML §4.2.3 makes the first
/// <c>&lt;base href&gt;</c> in document order the base for the <em>whole</em> document, including
/// the resources declared above it, so a scan that resolved as it went would resolve the head's
/// resources against the wrong base whenever <c>&lt;base&gt;</c> came late. The pass collects raw
/// values and resolves them at the end; the pass is microseconds and the requests it triggers are
/// milliseconds, so there is nothing to gain by resolving early.
/// </para>
/// </remarks>
public static class PreloadScanner
{
    /// <summary>
    /// Tag-name prefixes that could introduce a candidate. Used by
    /// <see cref="MightContainCandidates"/> to decide whether the tokenizer pass is worth starting
    /// at all.
    /// </summary>
    private static readonly string[] CandidateMarkers =
        ["<script", "<link", "<img", "<iframe", "<frame", "<object", "<embed", "<input"];

    /// <summary>
    /// A cheap, deliberately over-permissive test for whether a document can name any sub-resource.
    /// False means the tokenizer pass would certainly find nothing, so a document with no external
    /// resources — which most of the WPT corpus is — pays a handful of vectorized substring
    /// searches instead of a full tokenization on a pool thread.
    /// </summary>
    public static bool MightContainCandidates(string? html) =>
        !string.IsNullOrEmpty(html) &&
        CandidateMarkers.Any(marker => html.Contains(marker, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Scans <paramref name="html"/> and returns the sub-resources it names, resolved against the
    /// document's own <c>&lt;base href&gt;</c> where it has one and <paramref name="pageUrl"/>
    /// otherwise.
    /// </summary>
    /// <param name="html">The document source. Never mutated; this is a pure function of it.</param>
    /// <param name="pageUrl">The document's own URL, used as the base when it declares none.</param>
    public static PreloadScanResult Scan(string? html, string? pageUrl)
    {
        if (string.IsNullOrEmpty(html))
            return PreloadScanResult.Empty;

        var raw = new List<PreloadCandidate>();
        var seen = new HashSet<(PreloadKind, string)>();
        string? baseHref = null;

        // Depth counters rather than booleans: `<template><template>` is legal, and an inner
        // element's end tag must not re-open the outer one's contents to the scan.
        var inertDepth = 0;

        foreach (var token in new HtmlTokenizer().Tokenize(html))
        {
            if (token.Type == TokenType.EndTag)
            {
                if (inertDepth > 0 && IsInertContainer(token.Name))
                    inertDepth--;
                continue;
            }

            if (token.Type != TokenType.StartTag || token.Name is null)
                continue;

            if (IsInertContainer(token.Name))
            {
                // A self-closing `<template/>` has no contents to skip. The tokenizer reports the
                // syntax faithfully even though HTML ignores it on a non-void element, and treating
                // it as an open container would swallow the rest of the document.
                if (!token.SelfClosing)
                    inertDepth++;
                continue;
            }

            if (inertDepth > 0)
                continue;

            if (baseHref is null &&
                string.Equals(token.Name, "base", StringComparison.OrdinalIgnoreCase) &&
                Attribute(token, "href") is { Length: > 0 } declaredBase)
            {
                baseHref = declaredBase;
                continue;
            }

            if (CandidateFor(token) is not { } candidate)
                continue;

            if (seen.Add((candidate.Kind, candidate.RawUrl)))
                raw.Add(candidate with { Nonce = Attribute(token, "nonce") });
        }

        var documentBase = ResolveDocumentBase(baseHref, pageUrl);
        return new PreloadScanResult(
            documentBase,
            [.. raw.Select(c => c with { ResolvedUrl = UrlResolver.Resolve(c.RawUrl, documentBase)?.AbsoluteUri })]);
    }

    /// <summary>
    /// Elements whose contents are never loaded: <c>&lt;template&gt;</c> is inert until cloned, and
    /// a <c>&lt;noscript&gt;</c> body is text in a scripting-enabled document.
    /// </summary>
    private static bool IsInertContainer(string? name) =>
        string.Equals(name, "template", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(name, "noscript", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Classifies one start tag, or returns <c>null</c> when it names no fetchable sub-resource.
    /// The <see cref="PreloadCandidate.ResolvedUrl"/> is filled in by the caller once the whole
    /// document's base is known.
    /// </summary>
    private static PreloadCandidate? CandidateFor(HtmlToken tag)
    {
        // Switched on directly rather than lower-cased first: HtmlTokenizer lower-cases every tag
        // name as it reads it, and a scan over a large document is one of the few places where an
        // allocation per element would be worth noticing.
        switch (tag.Name)
        {
            case "script":
                // A `<script>` whose type is neither a JavaScript MIME essence nor `module` is a
                // data block, and the load path never fetches its src. Speculating on it would be a
                // round trip the document does not make.
                return ScriptMimeType.IsExecutable(Attribute(tag, "type"))
                    ? Candidate(PreloadKind.Script, Attribute(tag, "src"))
                    : null;

            case "link":
                return LinkCandidate(tag);

            case "img":
                return Candidate(PreloadKind.Image, Attribute(tag, "src"));

            case "input":
                return string.Equals(Attribute(tag, "type"), "image", StringComparison.OrdinalIgnoreCase)
                    ? Candidate(PreloadKind.Image, Attribute(tag, "src"))
                    : null;

            case "iframe":
                // `srcdoc` wins over `src` (HTML §4.8.5), and it is inline content with no request
                // to overlap.
                return Attribute(tag, "srcdoc") is not null
                    ? null
                    : Candidate(PreloadKind.Frame, Attribute(tag, "src"));

            case "frame":
            case "embed":
                return Candidate(PreloadKind.Frame, Attribute(tag, "src"));

            case "object":
                return Candidate(PreloadKind.Frame, Attribute(tag, "data"));

            default:
                return null;
        }
    }

    /// <summary>
    /// Classifies a <c>&lt;link&gt;</c> by its <c>rel</c> keywords. <c>rel</c> is a space-separated
    /// token list, so <c>rel="alternate stylesheet"</c> and <c>rel="preload"</c> are both matched by
    /// token rather than by string equality.
    /// </summary>
    /// <remarks>
    /// A <c>preload</c> whose <c>as</c> names a family this scanner does not model is skipped rather
    /// than guessed at: the whole point of <c>as</c> is that the request's headers and priority
    /// differ per destination, and a prefetch issued under the wrong one is not the request the
    /// consume site will make.
    /// </remarks>
    private static PreloadCandidate? LinkCandidate(HtmlToken tag)
    {
        var href = Attribute(tag, "href");
        if (href is null)
            return null;

        var rel = Attribute(tag, "rel");
        if (rel is null)
            return null;

        var keywords = rel.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (keywords.Contains("stylesheet", StringComparer.OrdinalIgnoreCase))
            return Candidate(PreloadKind.StyleSheet, href);

        if (keywords.Contains("modulepreload", StringComparer.OrdinalIgnoreCase))
            return Candidate(PreloadKind.Script, href);

        if (!keywords.Contains("preload", StringComparer.OrdinalIgnoreCase))
            return null;

        return Attribute(tag, "as")?.ToLowerInvariant() switch
        {
            "script" => Candidate(PreloadKind.Script, href),
            "style" => Candidate(PreloadKind.StyleSheet, href),
            "image" => Candidate(PreloadKind.Image, href),
            _ => null,
        };
    }

    private static PreloadCandidate? Candidate(PreloadKind kind, string? url) =>
        IsFetchable(url) ? new PreloadCandidate(kind, url!, ResolvedUrl: null) : null;

    /// <summary>
    /// Whether a URL names something a round trip could be saved on. Inline forms carry their own
    /// payload, <c>javascript:</c> is evaluated rather than fetched, a <c>blob:</c> is already in
    /// memory, and a bare fragment addresses the current document.
    /// </summary>
    private static bool IsFetchable(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url[0] == '#')
            return false;

        foreach (var scheme in (ReadOnlySpan<string>)["data:", "about:", "javascript:", "blob:"])
        {
            if (url.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    /// <summary>
    /// The base every relative URL in the document resolves against. A <c>&lt;base href&gt;</c> may
    /// itself be relative, so it is resolved against the page URL first; one that cannot be resolved
    /// leaves the page URL in place, which is what the document would fall back to.
    /// </summary>
    private static string? ResolveDocumentBase(string? baseHref, string? pageUrl) =>
        baseHref is null
            ? pageUrl
            : UrlResolver.Resolve(baseHref, pageUrl)?.AbsoluteUri ?? pageUrl;

    private static string? Attribute(HtmlToken tag, string name) =>
        tag.Attributes.TryGetValue(name, out var value) ? value : null;
}
