using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Broiler.HtmlBridge.Tests;

public class SubResourcePrefetcherTests
{
    [Fact]
    public async Task AsyncPrefetchAndConsumeAsync_ReturnsContent()
    {
        var fetchCount = 0;
        using var prefetcher = new SubResourcePrefetcher(async (url, token) =>
        {
            Interlocked.Increment(ref fetchCount);
            await Task.Yield();
            return $"content of {url}";
        });

        prefetcher.Prefetch(["https://example.com/a.css", "https://example.com/b.css"]);

        var a = await prefetcher.ConsumeAsync("https://example.com/a.css");
        var b = await prefetcher.ConsumeAsync("https://example.com/b.css");

        Assert.Equal("content of https://example.com/a.css", a);
        Assert.Equal("content of https://example.com/b.css", b);
        Assert.Equal(2, fetchCount);
    }

    [Fact]
    public void AsyncPrefetchAndSyncConsume_ReturnsContent()
    {
        using var prefetcher = new SubResourcePrefetcher(async (url, token) =>
        {
            await Task.Delay(10, token);
            return $"sync:{url}";
        });

        prefetcher.Prefetch(["https://example.com/sync.css"]);
        var content = prefetcher.Consume("https://example.com/sync.css");

        Assert.Equal("sync:https://example.com/sync.css", content);
    }

    [Fact]
    public async Task BoundedConcurrency_EnforcesMaxPerHost()
    {
        const int maxConcurrent = 4;
        const int totalRequests = 16;
        var currentRunning = 0;
        var peakRunning = 0;
        var completed = 0;
        var sync = new object();

        using var prefetcher = new SubResourcePrefetcher(async (url, token) =>
        {
            var cur = Interlocked.Increment(ref currentRunning);
            lock (sync)
            {
                if (cur > peakRunning)
                    peakRunning = cur;
            }

            await Task.Delay(20, token);
            Interlocked.Decrement(ref currentRunning);
            Interlocked.Increment(ref completed);
            return url;
        }, maxConcurrentRequestsPerHost: maxConcurrent);

        var urls = new List<string>();
        for (var i = 0; i < totalRequests; i++)
            urls.Add($"https://cdn.example.com/item{i}.js");

        prefetcher.Prefetch(urls);

        for (var i = 0; i < totalRequests; i++)
        {
            var result = await prefetcher.ConsumeAsync(urls[i]);
            Assert.Equal(urls[i], result);
        }

        Assert.Equal(totalRequests, completed);
        Assert.True(peakRunning <= maxConcurrent, $"Peak concurrency {peakRunning} exceeded max {maxConcurrent}");
        Assert.True(peakRunning > 1, $"Peak concurrency was {peakRunning}, expected multiple concurrent requests");
    }

    [Fact]
    public async Task MultipleHosts_HaveSeparateConcurrencyBuckets()
    {
        const int maxConcurrent = 2;
        var runningHostA = 0;
        var runningHostB = 0;
        var peakHostA = 0;
        var peakHostB = 0;
        var sync = new object();

        using var prefetcher = new SubResourcePrefetcher(async (url, token) =>
        {
            if (url.Contains("host-a.com"))
            {
                var cur = Interlocked.Increment(ref runningHostA);
                lock (sync)
                {
                    if (cur > peakHostA) peakHostA = cur;
                }
                await Task.Delay(25, token);
                Interlocked.Decrement(ref runningHostA);
            }
            else
            {
                var cur = Interlocked.Increment(ref runningHostB);
                lock (sync)
                {
                    if (cur > peakHostB) peakHostB = cur;
                }
                await Task.Delay(25, token);
                Interlocked.Decrement(ref runningHostB);
            }
            return url;
        }, maxConcurrentRequestsPerHost: maxConcurrent);

        var urls = new List<string>
        {
            "https://host-a.com/1", "https://host-a.com/2", "https://host-a.com/3", "https://host-a.com/4",
            "https://host-b.com/1", "https://host-b.com/2", "https://host-b.com/3", "https://host-b.com/4"
        };

        prefetcher.Prefetch(urls);

        foreach (var url in urls)
            await prefetcher.ConsumeAsync(url);

        Assert.True(peakHostA <= maxConcurrent);
        Assert.True(peakHostB <= maxConcurrent);
    }

    [Fact]
    public async Task Deduplication_FetchesSameUrlOnce()
    {
        var fetchCount = 0;
        using var prefetcher = new SubResourcePrefetcher(async (url, token) =>
        {
            Interlocked.Increment(ref fetchCount);
            await Task.Yield();
            return "same";
        });

        prefetcher.Prefetch(["https://example.com/dup.css", "https://example.com/dup.css"]);
        prefetcher.Prefetch(["https://example.com/dup.css"]);

        var c1 = await prefetcher.ConsumeAsync("https://example.com/dup.css");
        var c2 = await prefetcher.ConsumeAsync("https://example.com/dup.css");

        Assert.Equal("same", c1);
        Assert.Equal("same", c2);
        Assert.Equal(1, fetchCount);
    }

    [Fact]
    public async Task FetchFailure_IsCachedAsNull()
    {
        var fetchAttempts = 0;
        using var prefetcher = new SubResourcePrefetcher(async (url, token) =>
        {
            Interlocked.Increment(ref fetchAttempts);
            await Task.Yield();
            throw new InvalidOperationException("network error");
        });

        prefetcher.Prefetch(["https://example.com/fail.css"]);
        var result1 = await prefetcher.ConsumeAsync("https://example.com/fail.css");
        var result2 = await prefetcher.ConsumeAsync("https://example.com/fail.css");

        Assert.Null(result1);
        Assert.Null(result2);
        Assert.Equal(1, fetchAttempts);
    }

    [Fact]
    public async Task Cancellation_CancelsQueuedWork()
    {
        using var cts = new CancellationTokenSource();
        var startedCount = 0;

        using var prefetcher = new SubResourcePrefetcher(async (url, token) =>
        {
            Interlocked.Increment(ref startedCount);
            await Task.Delay(200, token);
            return url;
        }, maxConcurrentRequestsPerHost: 1, cancellationToken: cts.Token);

        prefetcher.Prefetch(["https://example.com/1", "https://example.com/2", "https://example.com/3"]);
        // Cancel before 2 and 3 can acquire the semaphore gate
        cts.Cancel();

        var res2 = await prefetcher.ConsumeAsync("https://example.com/2");
        var res3 = await prefetcher.ConsumeAsync("https://example.com/3");

        Assert.Null(res2);
        Assert.Null(res3);
        // Only request 1 should have started before cancellation occurred
        Assert.True(startedCount <= 2);
    }

    [Fact]
    public async Task Dispose_CancelsInFlightAndPending()
    {
        var prefetcher = new SubResourcePrefetcher(async (url, token) =>
        {
            await Task.Delay(500, token);
            return url;
        });

        prefetcher.Prefetch(["https://example.com/dispose1", "https://example.com/dispose2"]);
        prefetcher.Dispose();

        var res = await prefetcher.ConsumeAsync("https://example.com/dispose1");
        Assert.Null(res);
    }

    [Fact]
    public async Task RequestPrefetcher_MatchesRequestShape()
    {
        using var prefetcher = new RequestPrefetcher<string>(
            async (url, req, token) =>
            {
                await Task.Yield();
                return $"{url} for {req}";
            },
            (a, b) => string.Equals(a, b, StringComparison.Ordinal));

        prefetcher.Prefetch([("https://example.com/sheet.css", "anonymous")]);

        // Same shape consumes prefetched content
        var (consumed1, content1) = await prefetcher.TryConsumeAsync("https://example.com/sheet.css", "anonymous");
        Assert.True(consumed1);
        Assert.Equal("https://example.com/sheet.css for anonymous", content1);

        // Different shape refuses prefetch
        var (consumed2, content2) = await prefetcher.TryConsumeAsync("https://example.com/sheet.css", "use-credentials");
        Assert.False(consumed2);
        Assert.Null(content2);
    }

    [Theory]
    [InlineData("http/1.1", 6)]
    [InlineData("h2", 16)]
    [InlineData("", 16)]
    public void ResourceLoader_SelectsConcurrencyBudgetBasedOnProtocol(string protocol, int expectedCap)
    {
        var loader = new Dom.Runtime.ResourceLoader();
        if (!string.IsNullOrEmpty(protocol))
        {
            var timing = Net.DocumentFetchTiming.StartNavigation();
            timing.RecordResponseVersion(protocol == "h2" ? Version.Parse("2.0") : Version.Parse("1.1"));
            loader.DocumentFetchTiming = timing;
        }

        Assert.Equal(expectedCap, loader.PrefetcherConcurrencyCap);
    }
}
