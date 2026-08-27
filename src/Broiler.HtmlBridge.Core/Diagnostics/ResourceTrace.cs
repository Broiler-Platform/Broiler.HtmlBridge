using System;
using System.Diagnostics;

namespace Broiler.HtmlBridge.Core.Diagnostics;

/// <summary>
/// What a traced resource is. A host files an entry by this, so the distinctions that matter are the
/// ones that change where a reader would look for it — not the transport that produced it.
/// </summary>
internal enum ResourceTraceKind
{
    /// <summary>A top-level document: the page itself, or one the host navigated to before rendering.</summary>
    Document,

    /// <summary>An external <c>&lt;script src&gt;</c>, as fetched.</summary>
    Script,

    /// <summary>
    /// A script body as executed — inline, <c>data:</c>-URI or module. Never fetched, and the reason
    /// this kind exists: it is the code that actually ran, under the label the error log names it by.
    /// </summary>
    ExecutedScript,

    /// <summary>An external stylesheet, or another text sub-resource loaded through the document's loader.</summary>
    Stylesheet,

    /// <summary>A response to <c>fetch()</c>, <c>XMLHttpRequest</c> or <c>navigator.sendBeacon</c>.</summary>
    Fetch,

    /// <summary>An <c>&lt;iframe&gt;</c>/<c>&lt;object&gt;</c> sub-document.</summary>
    SubDocument,
}

/// <summary>One resource the engine fetched or executed, with the outcome.</summary>
internal sealed class ResourceTraceEntry
{
    /// <summary>The absolute URL, or — for an executed script — the page URL with the label appended.</summary>
    public required string Url { get; init; }

    /// <summary>What the resource is.</summary>
    public required ResourceTraceKind Kind { get; init; }

    /// <summary>The script label (<c>inline-3</c>, <c>deferred-0</c>, …) when the entry is one; otherwise null.</summary>
    public string? Label { get; init; }

    /// <summary>The decoded text, or null when the resource was binary, missing, or the attempt failed.</summary>
    public string? Content { get; init; }

    /// <summary>HTTP status, when the attempt reached one.</summary>
    public int? StatusCode { get; init; }

    /// <summary>Response content type, when known.</summary>
    public string? ContentType { get; init; }

    /// <summary>HTTP method, for entries where it is not <c>GET</c>.</summary>
    public string? Method { get; init; }

    /// <summary>Wall-clock time the attempt took.</summary>
    public double ElapsedMs { get; init; }

    /// <summary>The failure message when the attempt failed; null on success.</summary>
    public string? Error { get; init; }

    /// <summary>UTC time the attempt finished.</summary>
    public DateTime Timestamp { get; init; }
}

/// <summary>
/// An opt-in, off-by-default record of every resource the engine fetches or executes, readable by the
/// host above it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why here.</b> The fetches are spread across three assemblies — external scripts in
/// <c>Broiler.HtmlBridge.Core</c>, stylesheets/<c>fetch</c>/sub-documents in
/// <c>Broiler.HtmlBridge.Dom</c>, the top-level document in the CLI — and none of them may reference
/// the host that wants to write them out. This assembly is below all three, so a call made at a fetch
/// site can be read by the host, exactly as <see cref="BridgePhaseTrace"/> does for phase timings.
/// </para>
/// <para>
/// <b>Cost when off, which is always in production.</b> <see cref="Begin"/> reads one static field and
/// returns a <see langword="default"/> struct whose members are a null check; nothing is timed,
/// allocated, or decoded. A call site therefore pays for the trace only while a host is listening.
/// </para>
/// <para>
/// <b>A sink cannot break a render.</b> Recording is diagnostics: a listener that throws — a full disk,
/// a path that vanished — must not change what the page renders to or what the process exits with, so
/// <see cref="Publish"/> swallows what a handler throws. This is the one place that guarantee can be
/// made for every call site at once.
/// </para>
/// </remarks>
internal static class ResourceTrace
{
    /// <summary>
    /// Raised for each traced resource, on the thread that fetched it — which is a prefetch worker as
    /// often as it is the main thread, so a handler must be thread-safe and quick.
    /// </summary>
    public static event Action<ResourceTraceEntry>? Recorded;

    /// <summary>Whether anything is listening. False in every run that did not ask for a trace.</summary>
    public static bool IsActive => Recorded is not null;

    /// <summary>
    /// Starts timing one attempt, or returns the inactive <see langword="default"/> when no host is
    /// listening. The caller then reports the outcome through <see cref="Attempt.Completed"/> or
    /// <see cref="Attempt.Failed(Exception)"/>.
    /// </summary>
    public static Attempt Begin(ResourceTraceKind kind, string url) =>
        Recorded is null ? default : new Attempt(kind, url);

    /// <summary>
    /// Records a body that was never fetched — an inline script, a decoded <c>data:</c> URI, the
    /// serialized DOM at the end of a run — so it is archived beside the resources that were.
    /// </summary>
    public static void RecordBody(ResourceTraceKind kind, string url, string? content, string? label = null)
    {
        if (Recorded is null)
            return;

        Publish(new ResourceTraceEntry
        {
            Url = url,
            Kind = kind,
            Label = label,
            Content = content,
            Timestamp = DateTime.UtcNow,
        });
    }

    private static void Publish(ResourceTraceEntry entry)
    {
        var handler = Recorded;
        if (handler is null)
            return;

        try
        {
            handler(entry);
        }
        catch (Exception)
        {
            // See the type remarks: a diagnostics sink is never allowed to change the outcome of the
            // render it is observing.
        }
    }

    /// <summary>
    /// One in-flight attempt. <see langword="default"/> is the inactive form: every member returns
    /// without touching anything, which is what a call site gets when no host is listening.
    /// </summary>
    internal readonly struct Attempt
    {
        private readonly ResourceTraceKind _kind;
        private readonly string? _url;
        private readonly long _startTimestamp;

        internal Attempt(ResourceTraceKind kind, string url)
        {
            _kind = kind;
            _url = url;
            _startTimestamp = Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// The attempt finished. <paramref name="content"/> is null for a resource that was missing or
        /// whose bytes are not text — the entry is still recorded, because "asked for, got nothing" is
        /// the outcome a reader most wants to see.
        /// </summary>
        public void Completed(
            string? content,
            int? statusCode = null,
            string? contentType = null,
            string? method = null)
        {
            if (_url is null)
                return;

            Publish(new ResourceTraceEntry
            {
                Url = _url,
                Kind = _kind,
                Content = content,
                StatusCode = statusCode,
                ContentType = contentType,
                Method = method,
                ElapsedMs = ElapsedMs(),
                Timestamp = DateTime.UtcNow,
            });
        }

        /// <summary>The attempt threw.</summary>
        public void Failed(Exception exception) => Failed(exception.Message);

        /// <summary>The attempt failed with <paramref name="error"/>.</summary>
        public void Failed(string error)
        {
            if (_url is null)
                return;

            Publish(new ResourceTraceEntry
            {
                Url = _url,
                Kind = _kind,
                Error = error,
                ElapsedMs = ElapsedMs(),
                Timestamp = DateTime.UtcNow,
            });
        }

        private double ElapsedMs() =>
            (Stopwatch.GetTimestamp() - _startTimestamp) * 1000.0 / Stopwatch.Frequency;
    }
}
