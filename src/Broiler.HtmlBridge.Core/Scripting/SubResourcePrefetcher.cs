using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Broiler.HtmlBridge;

/// <summary>
/// Issues sub-resource fetches concurrently ahead of the synchronous call sites that consume
/// them.
/// </summary>
/// <remarks>
/// <para>
/// Every sub-resource call site in the bridge — external scripts, stylesheets, iframes,
/// <c>fetch()</c> — blocks on <c>GetAwaiter().GetResult()</c>, one resource at a time, so a page
/// with twenty sub-resources pays twenty serial round trips. The answer is not to make
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
/// bytes to the next and would be exactly the kind of unsynchronised shared cache that must stay
/// off the render path. An instance is created by whoever owns the document and dies with it.
/// </para>
/// <para>
/// <b>Fetch failures are cached as failures.</b> A prefetch that throws yields <c>null</c>, and
/// the consumer treats that exactly as its own failed fetch would — a resource is attempted
/// once per document.
/// </para>
/// </remarks>
public sealed class SubResourcePrefetcher : IDisposable
{
    /// <summary>
    /// Legacy per-host concurrency cap for HTTP/1.1 connections. Six is the browser convention
    /// for HTTP/1.1; more than this is rude to the origin and, past the connection limit, buys nothing.
    /// </summary>
    public const int DefaultHttp1MaxConcurrentRequestsPerHost = 6;

    /// <summary>
    /// Concurrent requests allowed per host for multiplexed HTTP/2 or profile-managed transports.
    /// Accommodates parallel streams without occupying unbounded resources or thread workers.
    /// </summary>
    public const int DefaultMaxConcurrentRequestsPerHost = 16;

    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, CancellationToken, Task<string?>> _fetchAsync;
    private readonly int _maxConcurrentRequestsPerHost;
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationToken _externalToken;
    private int _disposed;

    /// <param name="fetchAsync">The asynchronous fetch function this prefetcher parallelizes.</param>
    /// <param name="maxConcurrentRequestsPerHost">Per-host concurrency cap.</param>
    /// <param name="cancellationToken">Cancellation token tied to document/session lifetime.</param>
    public SubResourcePrefetcher(
        Func<string, CancellationToken, Task<string?>> fetchAsync,
        int maxConcurrentRequestsPerHost = DefaultMaxConcurrentRequestsPerHost,
        CancellationToken cancellationToken = default)
    {
        _fetchAsync = fetchAsync ?? throw new ArgumentNullException(nameof(fetchAsync));
        _maxConcurrentRequestsPerHost = Math.Max(1, maxConcurrentRequestsPerHost);
        _externalToken = cancellationToken;
    }

    /// <summary>The per-host concurrency cap configured for this prefetcher.</summary>
    public int MaxConcurrentRequestsPerHost => _maxConcurrentRequestsPerHost;

    /// <param name="fetchAsync">The task-based asynchronous fetch function this prefetcher parallelizes.</param>
    /// <param name="maxConcurrentRequestsPerHost">Per-host concurrency cap.</param>
    /// <param name="cancellationToken">Cancellation token tied to document/session lifetime.</param>
    public SubResourcePrefetcher(
        Func<string, Task<string?>> fetchAsync,
        int maxConcurrentRequestsPerHost = DefaultMaxConcurrentRequestsPerHost,
        CancellationToken cancellationToken = default)
        : this(fetchAsync is null ? null! : (url, _) => fetchAsync(url), maxConcurrentRequestsPerHost, cancellationToken)
    {
    }

    /// <param name="fetch">
    /// The blocking fetch this prefetcher parallelizes — the very function the consuming call site
    /// would otherwise have run inline. Taking it as a parameter is what lets scripts, stylesheets
    /// and sub-documents share one prefetcher without this type knowing any of their policy
    /// (CSP checks, WPT host mapping, file:// handling) — that all stays in the caller.
    /// </param>
    /// <param name="maxConcurrentRequestsPerHost">Per-host concurrency cap.</param>
    /// <param name="cancellationToken">Cancellation token tied to document/session lifetime.</param>
    public SubResourcePrefetcher(
        Func<string, string?> fetch,
        int maxConcurrentRequestsPerHost = DefaultMaxConcurrentRequestsPerHost,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        _fetchAsync = (url, token) =>
        {
            token.ThrowIfCancellationRequested();
            return Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return fetch(url);
            }, token);
        };
        _maxConcurrentRequestsPerHost = Math.Max(1, maxConcurrentRequestsPerHost);
        _externalToken = cancellationToken;
    }

    /// <summary>Number of URLs this prefetcher has issued a request for. For tests and diagnostics.</summary>
    public int RequestedCount => _entries.Count;

    /// <summary>
    /// Starts a fetch for each URL that does not already have one. Returns immediately; nothing
    /// here waits. Duplicate URLs — the same script included twice, a stylesheet linked from two
    /// places — collapse onto one request, which is what makes prefetching safe to call
    /// generously.
    /// </summary>
    public void Prefetch(IEnumerable<string> urls) => Prefetch(urls, CancellationToken.None);

    /// <summary>
    /// Starts a fetch for each URL with the provided cancellation token.
    /// </summary>
    public void Prefetch(IEnumerable<string> urls, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(urls);
        if (_disposed != 0)
            return;

        foreach (var url in urls)
        {
            // Reading .Value is what starts the request. Creating the entry alone would leave every
            // fetch to be forced by its own Consume — i.e. exactly the serial behaviour this
            // replaces, with extra machinery.
            if (!string.IsNullOrWhiteSpace(url))
                _ = EntryFor(url, cancellationToken).Value;
        }
    }

    /// <summary>
    /// Asynchronously returns the content for <paramref name="url"/>.
    /// </summary>
    public async Task<string?> ConsumeAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var task = EntryFor(url, cancellationToken).Value;
        if (cancellationToken.CanBeCanceled)
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);

        return await task.ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the content for <paramref name="url"/>, blocking until it arrives. A prefetched URL
    /// has been in flight since <see cref="Prefetch(IEnumerable{string})"/>; one that was never prefetched is fetched
    /// now, so a call site behaves identically whether or not a prefetch ran — which is what keeps
    /// this safe to adopt one call site at a time.
    /// </summary>
    public string? Consume(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        // Not ConfigureAwait-able through a Lazy; GetAwaiter().GetResult() on a task that was
        // started with Task.Run or async I/O never has a captured context to deadlock against.
        // The wait for the network is not the running task's time.
        using var waiting = TaskClock.Waiting();
        return EntryFor(url, CancellationToken.None).Value.GetAwaiter().GetResult();
    }

    /// <summary>True when a request for <paramref name="url"/> has already been issued.</summary>
    public bool IsPending(string url) => _entries.ContainsKey(url);

    private Lazy<Task<string?>> EntryFor(string url, CancellationToken cancellationToken) =>
        // LazyThreadSafetyMode.ExecutionAndPublication: GetOrAdd may run the factory more than
        // once under contention, and each extra run would be an extra HTTP request. The Lazy makes
        // the losing factory's value cheap to discard — only the published one ever starts a task.
        _entries.GetOrAdd(
            url,
            static (key, state) => new Lazy<Task<string?>>(
                () => state.self.StartFetch(key, state.cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication),
            (self: this, cancellationToken));

    private async Task<string?> StartFetch(string url, CancellationToken cancellationToken)
    {
        var gate = _hostGates.GetOrAdd(
            HostOf(url),
            static (_, maxPerHost) => new SemaphoreSlim(maxPerHost, maxPerHost),
            _maxConcurrentRequestsPerHost);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            _cts.Token, _externalToken, cancellationToken);
        var token = linked.Token;

        try
        {
            await gate.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }

        try
        {
            token.ThrowIfCancellationRequested();
            return await _fetchAsync(url, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch
        {
            // A failed sub-resource is not a failed page: the consumer sees null and applies
            // whatever it already did for a fetch that came back empty.
            return null;
        }
        finally
        {
            try { gate.Release(); }
            catch (ObjectDisposedException) { }
        }
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

    /// <summary>
    /// Cancels all pending and in-flight prefetch requests and releases gate resources.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try { _cts.Cancel(); }
        catch (ObjectDisposedException) { }
        _cts.Dispose();

        foreach (var gate in _hostGates.Values)
        {
            try { gate.Dispose(); }
            catch (ObjectDisposedException) { }
        }
    }
}
