using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Broiler.HtmlBridge;

/// <summary>
/// Issues sub-resource fetches concurrently ahead of the synchronous call sites that consume
/// them. Multithreading roadmap item #2.
/// </summary>
/// <remarks>
/// <para>
/// Every sub-resource call site in the bridge — external scripts, stylesheets, iframes,
/// <c>fetch()</c> — blocks on <c>GetAwaiter().GetResult()</c>, one resource at a time, so a page
/// with twenty sub-resources pays twenty serial round trips. The roadmap's answer is not to make
/// those call sites <c>async</c> (they are ordering-sensitive: <c>document.write</c> and script
/// execution order both depend on running in document order), but to split them in two:
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Prefetch.</b> When a set of URLs becomes known — the moment a document's scripts have been
/// scanned, say — issue them all at once, bounded per host.
/// </description></item>
/// <item><description>
/// <b>Consume.</b> The call site stays exactly where it was, synchronous and in document order,
/// but now blocks on a request that has been in flight since the scan instead of starting one.
/// </description></item>
/// </list>
/// <para>
/// The win is latency, not CPU: the round trips overlap. Ordering is untouched, because the
/// consume step still happens at the original call site in the original order — the cache only
/// changes when a request <em>starts</em>, never when its result is used.
/// </para>
/// <para>
/// <b>Lifetime is per document, deliberately.</b> A process-wide cache would serve one document's
/// bytes to the next and would be exactly the kind of unsynchronised shared cache the static-state
/// audit (<c>docs/architecture/multithreading-static-state.md</c>) exists to keep out of the
/// render path. An instance is created by whoever owns the document and dies with it.
/// </para>
/// <para>
/// <b>Fetch failures are cached as failures.</b> A prefetch that throws yields <c>null</c>, and
/// the consumer treats that exactly as its own failed fetch would have — a resource is attempted
/// once per document, as it was before.
/// </para>
/// </remarks>
public sealed class SubResourcePrefetcher
{
    /// <summary>
    /// Concurrent requests allowed per host. Six is the browser convention for HTTP/1.1 and the
    /// figure the roadmap names; more than this is rude to the origin and, past the connection
    /// limit, buys nothing.
    /// </summary>
    public const int DefaultMaxConcurrentRequestsPerHost = 6;

    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, string?> _fetch;
    private readonly int _maxConcurrentRequestsPerHost;

    /// <param name="fetch">
    /// The blocking fetch this prefetcher parallelizes — the very function the consuming call site
    /// would otherwise have run inline. Taking it as a parameter is what lets scripts, stylesheets
    /// and sub-documents share one prefetcher without this type knowing any of their policy
    /// (CSP checks, WPT host mapping, file:// handling) — that all stays in the caller.
    /// </param>
    /// <param name="maxConcurrentRequestsPerHost">Per-host concurrency cap.</param>
    public SubResourcePrefetcher(
        Func<string, string?> fetch,
        int maxConcurrentRequestsPerHost = DefaultMaxConcurrentRequestsPerHost)
    {
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _maxConcurrentRequestsPerHost = Math.Max(1, maxConcurrentRequestsPerHost);
    }

    /// <summary>Number of URLs this prefetcher has issued a request for. For tests and diagnostics.</summary>
    public int RequestedCount => _entries.Count;

    /// <summary>
    /// Starts a fetch for each URL that does not already have one. Returns immediately; nothing
    /// here waits. Duplicate URLs — the same script included twice, a stylesheet linked from two
    /// places — collapse onto one request, which is what makes prefetching safe to call
    /// generously.
    /// </summary>
    public void Prefetch(IEnumerable<string> urls)
    {
        ArgumentNullException.ThrowIfNull(urls);

        foreach (var url in urls)
        {
            // Reading .Value is what starts the request. Creating the entry alone would leave every
            // fetch to be forced by its own Consume — i.e. exactly the serial behaviour this
            // replaces, with extra machinery.
            if (!string.IsNullOrWhiteSpace(url))
                _ = EntryFor(url).Value;
        }
    }

    /// <summary>
    /// Returns the content for <paramref name="url"/>, blocking until it arrives. A prefetched URL
    /// has been in flight since <see cref="Prefetch"/>; one that was never prefetched is fetched
    /// now, so a call site behaves identically whether or not a prefetch ran — which is what keeps
    /// this safe to adopt one call site at a time.
    /// </summary>
    public string? Consume(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        // Not ConfigureAwait-able through a Lazy; GetAwaiter().GetResult() on a task that was
        // started with Task.Run never has a captured context to deadlock against.
        return EntryFor(url).Value.GetAwaiter().GetResult();
    }

    /// <summary>True when a request for <paramref name="url"/> has already been issued.</summary>
    public bool IsPending(string url) => _entries.ContainsKey(url);

    private Lazy<Task<string?>> EntryFor(string url) =>
        // LazyThreadSafetyMode.ExecutionAndPublication: GetOrAdd may run the factory more than
        // once under contention, and each extra run would be an extra HTTP request. The Lazy makes
        // the losing factory's value cheap to discard — only the published one ever starts a task.
        _entries.GetOrAdd(
            url,
            static (key, state) => new Lazy<Task<string?>>(
                () => state.StartFetch(key),
                LazyThreadSafetyMode.ExecutionAndPublication),
            this);

    private Task<string?> StartFetch(string url)
    {
        var gate = _hostGates.GetOrAdd(
            HostOf(url),
            static (_, maxPerHost) => new SemaphoreSlim(maxPerHost, maxPerHost),
            _maxConcurrentRequestsPerHost);

        return Task.Run(() =>
        {
            gate.Wait();
            try
            {
                return _fetch(url);
            }
            catch
            {
                // A failed sub-resource is not a failed page: the consumer sees null and applies
                // whatever it already did for a fetch that came back empty.
                return (string?)null;
            }
            finally
            {
                gate.Release();
            }
        });
    }

    /// <summary>
    /// The host a URL's concurrency is charged to. Anything unparseable — and every
    /// <c>file://</c> URL, which has no meaningful host — shares one bucket; local reads are not
    /// what the per-host cap is protecting.
    /// </summary>
    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : string.Empty;
}
