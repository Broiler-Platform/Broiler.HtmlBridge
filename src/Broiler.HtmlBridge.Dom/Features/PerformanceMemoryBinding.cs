using Broiler.JavaScript.BuiltIns.Number;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Storage;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The non-standard <c>MemoryInfo</c> object behind <c>performance.memory</c> and
/// <c>console.memory</c> — the JavaScript heap sizes Chrome exposes under those two names. Pure
/// static, like <c>CryptoBinding</c>: it reads the .NET GC's own counters and touches no bridge
/// state.
/// </summary>
/// <remarks>
/// <para>
/// The three numbers are read live, from the runtime that is actually executing the page's script:
/// the managed heap Broiler's JavaScript objects live in. They are not a stand-in for a V8 heap —
/// a page asking how much memory it has used gets Broiler's answer to that question.
/// </para>
/// <para>
/// Absent, the pair did not read as an unsupported non-standard extension: a page that reads
/// <c>performance.memory.usedJSHeapSize</c> threw "Cannot get property usedJSHeapSize of
/// undefined", which aborts the function that asked. Memory sampling sits in the same telemetry
/// preamble as the timing reads next to it, so the abort lands before the rest of that module runs.
/// </para>
/// <para>
/// The values are rounded to <see cref="GranularityBytes"/> before they leave the engine. Chrome
/// bucketizes them for the same reason: an exact heap size is a high-entropy, continuously varying
/// value, and a page that samples it repeatedly can use it to identify the browser instance.
/// Rounding keeps the answer useful for the size comparisons pages actually make while removing
/// the low bits that carry that entropy.
/// </para>
/// </remarks>
internal static class PerformanceMemoryBinding
{
    /// <summary>Reporting granularity, in bytes (100 KiB) — see the class remarks.</summary>
    private const long GranularityBytes = 100 * 1024;

    /// <summary>
    /// Builds one <c>MemoryInfo</c>. The properties are accessors rather than values because a page
    /// that samples memory does so repeatedly — with values it would read the same three numbers
    /// forever and conclude nothing had been allocated.
    /// </summary>
    public static JSObject Build()
    {
        var memory = new JSObject();

        memory.FastAddProperty("jsHeapSizeLimit",
            new DomFunction((in _) => new JSNumber(Sample().Limit), "get jsHeapSizeLimit"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        memory.FastAddProperty("totalJSHeapSize",
            new DomFunction((in _) => new JSNumber(Sample().Total), "get totalJSHeapSize"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        memory.FastAddProperty("usedJSHeapSize",
            new DomFunction((in _) => new JSNumber(Sample().Used), "get usedJSHeapSize"),
            null, JSPropertyAttributes.EnumerableConfigurableProperty);

        return memory;
    }

    /// <summary>
    /// One reading of the three sizes, taken together. They are derived from a single
    /// <see cref="GCMemoryInfo"/> so that they cannot contradict each other: read independently,
    /// the committed size and the allocated size are sampled a moment apart and the pair can come
    /// back with "used" above "total", which is not a state the heap is ever in.
    /// </summary>
    /// <returns>
    /// <c>Limit</c> — the ceiling an allocation would fail against, which is the container or
    /// cgroup limit where one is set and physical memory otherwise; <c>Total</c> — the committed
    /// heap, the analogue of V8's total heap size; <c>Used</c> — the bytes currently allocated in
    /// it. All three rounded to <see cref="GranularityBytes"/>.
    /// </returns>
    private static (double Limit, double Total, double Used) Sample()
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        long used = GC.GetTotalMemory(forceFullCollection: false);

        // HeapSizeBytes is the size as of the last collection, so allocations made since then can
        // put `used` above it. The committed heap is at least what is allocated in it.
        long total = Math.Max(info.HeapSizeBytes, used);
        long limit = info.TotalAvailableMemoryBytes > 0 ? info.TotalAvailableMemoryBytes : total;

        return (Round(limit), Round(total), Round(used));
    }

    private static double Round(long bytes) =>
        Math.Round(bytes / (double)GranularityBytes) * GranularityBytes;
}
