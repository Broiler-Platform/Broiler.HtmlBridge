using Broiler.HtmlBridge.Scripting;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Broiler.HtmlBridge;

/// <summary>
/// Runs a <see cref="PreloadScanner"/> pass on a worker thread while the main thread parses the same
/// document, and hands what it finds to a sink that issues the requests. Multithreading roadmap
/// item #17.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this buys, precisely.</b> Item #2 built the prefetch/consume split and wired it to the
/// two families whose URL set is already known at a single point: external scripts (the script scan)
/// and <c>&lt;link&gt;</c> stylesheets (the collected sheet list). Both of those points are reached
/// <em>after</em> work that does not depend on them — the stylesheet set is collected once the whole
/// document has been parsed and the style elements gathered, so a page's sheet requests start after
/// the parse rather than during it. This starts them from the raw source instead, before the parse
/// has produced a single node. The parse is CPU and the fetch is latency, so the two overlap
/// completely; nothing here makes the parse faster, and it is not meant to.
/// </para>
/// <para>
/// <b>Safe by construction, and that is the reason the roadmap rates it low risk.</b> The worker
/// reads one immutable string and writes nothing the document owns. It builds a list of URLs and
/// calls a sink; the sink's only job is to start requests, which the consume sites were going to
/// start anyway, in the same order, under the same keys. No DOM node, no ambient slot and no cache
/// of the render path is touched, so the ambient-state contract
/// (<c>docs/architecture/multithreading-static-state.md</c>) has nothing to establish here — this
/// worker never renders.
/// </para>
/// <para>
/// <b>Nothing waits on it.</b> A caller starts a scan and carries on; the result is available to
/// whoever wants it via <see cref="Result"/>, which blocks, but the wiring on the render path does
/// not read it at all — the sink runs on the worker. A scan that never finishes before the document
/// does costs one pool thread and is thrown away, and a scan that throws is swallowed: a
/// speculation that fails must leave the document exactly as it would have been without it, which
/// is a document whose consume sites each fetch their own resource inline.
/// </para>
/// <para>
/// <b>The exit gate.</b> <c>BROILER_PRELOAD_SCAN=0</c> turns the scan off entirely, and that is the
/// sequential path rather than an approximation of it: with no scan, no prefetcher entry exists for
/// any speculated URL and every consume site takes the branch it took before this type existed.
/// Output is identical because the scan changes only when a request <em>starts</em>, never which
/// request is made or what is done with the bytes.
/// </para>
/// </remarks>
public sealed class SpeculativePreloadScan
{
    /// <summary>
    /// Environment variable that disables the scan. Any of <c>0</c>, <c>false</c> and <c>off</c>
    /// (case-insensitive) turns it off; anything else, including absence, leaves it on.
    /// </summary>
    public const string EnabledEnvironmentVariable = "BROILER_PRELOAD_SCAN";

    private static bool _enabled = ReadConfiguredEnabled();
    private static long _scansStarted;
    private static long _scansSkipped;

    private readonly Task<PreloadScanResult> _scan;

    private SpeculativePreloadScan(Task<PreloadScanResult> scan) => _scan = scan;

    /// <summary>
    /// Whether documents get a speculative scan. Settable so a determinism harness can compare the
    /// two paths in one process; the default comes from
    /// <see cref="EnabledEnvironmentVariable"/>.
    /// </summary>
    public static bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>Scans actually started. For tests and the measurement harness.</summary>
    public static long ScansStarted => Interlocked.Read(ref _scansStarted);

    /// <summary>
    /// Scans not started because the document plainly names no sub-resource
    /// (<see cref="PreloadScanner.MightContainCandidates"/>) or the feature is off. Counted rather
    /// than inferred, because "the scan found nothing" and "the scan never ran" are different
    /// claims and only one of them costs a thread.
    /// </summary>
    public static long ScansSkipped => Interlocked.Read(ref _scansSkipped);

    /// <summary>Resets the counters. For tests, which assert on them.</summary>
    public static void ResetCounters()
    {
        Interlocked.Exchange(ref _scansStarted, 0);
        Interlocked.Exchange(ref _scansSkipped, 0);
    }

    /// <summary>
    /// Starts a scan of <paramref name="html"/> on a worker and returns immediately. Returns
    /// <c>null</c> when no scan was started — the feature is off, or the source names nothing
    /// fetchable — so a caller that wants the result can tell "nothing found" from "never ran".
    /// </summary>
    /// <param name="html">The document source, as handed to the parser.</param>
    /// <param name="pageUrl">The document's URL; the base for relative references and for CSP.</param>
    /// <param name="csp">
    /// The host's policy, if it configured one. It is checked <em>in addition to</em> any
    /// <c>&lt;meta http-equiv&gt;</c> policy the source declares, so a resource either policy
    /// forbids is never speculated on: putting a request on the wire is the thing the policy stops,
    /// and "fetched but not used" is not a defensible reading of it.
    /// </param>
    /// <param name="onCompleted">
    /// Runs on the worker as soon as the scan finishes, which is the whole point — it is what starts
    /// the requests while the parse is still running. Exceptions from it are swallowed with the
    /// rest of the scan.
    /// </param>
    public static SpeculativePreloadScan? Start(
        string? html,
        string? pageUrl,
        ContentSecurityPolicy? csp = null,
        Action<PreloadScanResult>? onCompleted = null)
    {
        if (!_enabled || !PreloadScanner.MightContainCandidates(html))
        {
            Interlocked.Increment(ref _scansSkipped);
            return null;
        }

        Interlocked.Increment(ref _scansStarted);

        return new SpeculativePreloadScan(Task.Run(() =>
        {
            PreloadScanResult result;
            try
            {
                result = Authorized(PreloadScanner.Scan(html, pageUrl), html!, pageUrl, csp);
            }
            catch
            {
                // A speculation that fails leaves the document exactly as it would have been: every
                // consume site fetches its own resource inline, as it did before this type existed.
                return PreloadScanResult.Empty;
            }

            try
            {
                onCompleted?.Invoke(result);
            }
            catch
            {
                // Same contract: a sink that throws must not take the document with it. The result
                // is still returned, so a caller reading it directly is unaffected.
            }

            return result;
        }));
    }

    /// <summary>
    /// The scan's findings, blocking until the worker finishes. The render path does not call this —
    /// the sink is what does the useful work — but a consumer that wants the URL set (item #8's
    /// image decode, item #16's compile-ahead queue) reads it here.
    /// </summary>
    public PreloadScanResult Result => _scan.GetAwaiter().GetResult();

    /// <summary>Whether the worker has finished. Never blocks.</summary>
    public bool IsCompleted => _scan.IsCompleted;

    /// <summary>
    /// Drops the candidates a Content Security Policy forbids, applying exactly the checks the
    /// consume sites apply — <c>AllowsExternalScript</c> for scripts and
    /// <c>AllowsExternalStyle</c> for stylesheets, against the raw attribute value, the page URL and
    /// the element's own nonce.
    /// </summary>
    /// <remarks>
    /// Images and sub-documents are passed through unfiltered because
    /// <see cref="ContentSecurityPolicy"/> models neither <c>img-src</c> nor <c>frame-src</c>. That
    /// is a match for the consume sites rather than a gap opened here: a policy this engine does not
    /// enforce on the load is not one a prefetch can be accused of evading. It becomes a filter to
    /// add here the day those directives are enforced anywhere.
    /// </remarks>
    private static PreloadScanResult Authorized(
        PreloadScanResult scanned,
        string html,
        string? pageUrl,
        ContentSecurityPolicy? configured)
    {
        var declared = ContentSecurityPolicy.FromHtml(html);
        if (configured is null && declared is null)
            return scanned;

        var allowed = new List<PreloadCandidate>(scanned.Candidates.Count);
        foreach (var candidate in scanned.Candidates)
        {
            if (Allows(configured, candidate, pageUrl) && Allows(declared, candidate, pageUrl))
                allowed.Add(candidate);
        }

        return new PreloadScanResult(scanned.DocumentBaseUrl, allowed);
    }

    private static bool Allows(ContentSecurityPolicy? csp, PreloadCandidate candidate, string? pageUrl) =>
        csp is null || candidate.Kind switch
        {
            PreloadKind.Script => csp.AllowsExternalScript(candidate.RawUrl, pageUrl, candidate.Nonce),
            PreloadKind.StyleSheet => csp.AllowsExternalStyle(candidate.RawUrl, pageUrl, candidate.Nonce),
            _ => true,
        };

    private static bool ReadConfiguredEnabled()
    {
        var configured = Environment.GetEnvironmentVariable(EnabledEnvironmentVariable);
        return string.IsNullOrWhiteSpace(configured) ||
            !(configured.Equals("0", StringComparison.Ordinal) ||
              configured.Equals("false", StringComparison.OrdinalIgnoreCase) ||
              configured.Equals("off", StringComparison.OrdinalIgnoreCase));
    }
}
