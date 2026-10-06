using Broiler.Net.Http;
using Broiler.Net.Sites;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Broiler.HtmlBridge;

/// <summary>
/// What one fetch of a document's looked like on the network: the stuff of the document's
/// <c>PerformanceResourceTiming</c> entry (Resource Timing §4).
/// </summary>
/// <remarks>
/// <para>
/// <b>What is measured, and what is not.</b> The fetch's start, the moment its last redirect was
/// answered, the moment its response's head arrived and the moment its body had all arrived are
/// measured, on <see cref="Stopwatch"/>'s clock -- the one <c>performance.now()</c> reads. The
/// connection's own phases (lookup, connect, TLS) are not observable through the transport, so an entry
/// reports them where a browser reports a reused connection's: at the fetch's start.
/// </para>
/// <para>
/// <b>The transport decompresses below what is seen here,</b> so a compressed response reports its
/// decoded size as its encoded size too, and no <c>Content-Encoding</c>.
/// </para>
/// <para>
/// <b>What a page may see is decided by the document, not here.</b> The record keeps everything the
/// fetch revealed, and whether it passed the timing-allow check and whether its response is the
/// document's to read; the entry hides what they do not allow.
/// </para>
/// </remarks>
public sealed record ResourceTimingRecord
{
    /// <summary>The URL first requested, which names the entry whatever it was redirected to.</summary>
    public required Uri Name { get; init; }

    /// <summary>What asked for the resource: <c>script</c>, <c>link</c>, <c>css</c>, <c>iframe</c>, <c>fetch</c>, ….</summary>
    public required string InitiatorType { get; init; }

    /// <summary>The document whose timeline the entry belongs to.</summary>
    public required DocumentRequestContext Client { get; init; }

    /// <summary>When the fetch began, as a <see cref="Stopwatch.GetTimestamp"/> value.</summary>
    public required long FetchStart { get; init; }

    /// <summary>When the last redirect was answered and the fetch of the final URL began; 0 when nothing redirected.</summary>
    public long RedirectEnd { get; init; }

    /// <summary>When the response's head arrived.</summary>
    public required long ResponseStart { get; init; }

    /// <summary>When the response's body had all arrived.</summary>
    public required long ResponseEnd { get; init; }

    /// <summary>
    /// Whether the fetch passed Fetch's timing-allow check at every hop: the document's own origin, or a
    /// <c>Timing-Allow-Origin</c> naming it or <c>*</c>. It exposes the fetch's timings, sizes and protocol.
    /// </summary>
    public bool TimingAllowed { get; init; }

    /// <summary>
    /// Whether the response is the document's to read: CORS-same-origin for a subresource, the document's
    /// own origin throughout for a frame's navigation. It exposes the response's status and type.
    /// </summary>
    public bool ResponseDetailsAllowed { get; init; }

    /// <summary>The response's status.</summary>
    public int ResponseStatus { get; init; }

    /// <summary>The essence of the response's MIME type, or the empty string.</summary>
    public string ContentType { get; init; } = string.Empty;

    /// <summary>The response's <c>Content-Encoding</c>, or the empty string.</summary>
    public string ContentEncoding { get; init; } = string.Empty;

    /// <summary>The body's size as it was transferred, before any content coding was undone.</summary>
    public long EncodedBodySize { get; init; }

    /// <summary>The body's size once its content coding was undone.</summary>
    public long DecodedBodySize { get; init; }

    /// <summary>The ALPN token of the protocol the response came over (<c>http/1.1</c>, <c>h2</c>, …).</summary>
    public string NextHopProtocol { get; init; } = string.Empty;

    /// <summary>Whether the response came over TLS: its final URL is <c>https</c>.</summary>
    public bool SecureConnection { get; init; }

    /// <summary>Whether the resource held up the document's rendering: a parser-inserted script or style sheet in its head.</summary>
    public bool RenderBlocking { get; init; }

    /// <summary>The response's <c>Server-Timing</c> header, or <see langword="null"/>.</summary>
    public string? ServerTiming { get; init; }
}

/// <summary>Where a fetch's <see cref="ResourceTimingRecord"/> goes once it has been measured.</summary>
public interface IResourceTimingSink
{
    /// <summary>Takes the record of a completed fetch. Called on whichever thread completed it.</summary>
    void Record(ResourceTimingRecord record);
}

/// <summary>
/// The records of fetches made for a document before it has a bridge -- the scripts its host extracted
/// and fetched (<see cref="ScriptExtractionResult.ResourceTimings"/>) -- for the host to hand to the
/// engine that runs the document (<c>ScriptEngine.DocumentResourceTimings</c>).
/// </summary>
public sealed class ResourceTimingLog : IResourceTimingSink
{
    private readonly ConcurrentQueue<ResourceTimingRecord> _records = new();

    /// <inheritdoc />
    public void Record(ResourceTimingRecord record) => _records.Enqueue(record ?? throw new ArgumentNullException(nameof(record)));

    /// <summary>Every record taken so far, in the order the fetches completed.</summary>
    public IReadOnlyList<ResourceTimingRecord> Records => _records.ToArray();
}

/// <summary>
/// One fetch to measure: what asked for it, for which document, and where its record goes. A loader
/// that is handed one times the fetch and records it; one that is not records nothing.
/// </summary>
internal sealed record ResourceTimingRequest(string InitiatorType, DocumentRequestContext Client, IResourceTimingSink Sink, bool RenderBlocking = false)
{
    /// <summary>
    /// Whether the fetch navigates a frame, whose response is judged by its URL's origin: a navigation's
    /// response is never CORS-tainted, whichever origin it came from.
    /// </summary>
    private bool IsNavigation => InitiatorType is "iframe" or "frame" or "object" or "embed";

    /// <summary>
    /// <paramref name="context"/> as the timed fetch is sent with it: its hop policy, which the transport
    /// asks before every hop, also notes in <paramref name="redirectEnd"/> when a redirect's next hop began.
    /// </summary>
    internal static RequestContext NotingRedirects(RequestContext context, StrongBox<long> redirectEnd)
    {
        var policy = context.HopPolicy;
        return context with
        {
            HopPolicy = (url, hop) =>
            {
                if (hop > 0)
                    redirectEnd.Value = Now();
                return policy is null || policy(url, hop);
            },
        };
    }

    /// <summary>
    /// The record of a fetch of <paramref name="url"/> that began at <paramref name="fetchStart"/>, whose
    /// last redirect was answered at <paramref name="redirectEnd"/> (0 for none), whose head arrived at
    /// <paramref name="responseStart"/> and whose body, <paramref name="decodedBytes"/> bytes once decoded,
    /// had all arrived at <paramref name="responseEnd"/>.
    /// </summary>
    internal ResourceTimingRecord Describe(
        Uri url, long fetchStart, long redirectEnd, long responseStart, long responseEnd, TransportResponse response, long decodedBytes)
    {
        var message = response.Message;
        var contentHeaders = message.Content.Headers;
        var encoding = contentHeaders.ContentEncoding.Count > 0 ? string.Join(", ", contentHeaders.ContentEncoding) : string.Empty;
        var encodedBytes = encoding.Length > 0 && contentHeaders.ContentLength is { } length ? length : decodedBytes;
        return new ResourceTimingRecord
        {
            Name = url,
            InitiatorType = InitiatorType,
            Client = Client,
            FetchStart = fetchStart,
            RedirectEnd = response.Redirected ? redirectEnd : 0,
            ResponseStart = responseStart,
            ResponseEnd = responseEnd,
            TimingAllowed = PassesTimingAllowCheck(response, Client),
            ResponseDetailsAllowed = IsNavigation
                ? response.UrlList.All(hop => Origin.FromUrl(hop).IsSameOrigin(Client.Origin))
                : response.Tainting is ResponseTainting.Basic or ResponseTainting.Cors,
            ResponseStatus = response.StatusCode,
            ContentType = contentHeaders.ContentType?.MediaType?.ToLowerInvariant() ?? string.Empty,
            ContentEncoding = encoding,
            EncodedBodySize = encodedBytes,
            DecodedBodySize = decodedBytes,
            NextHopProtocol = ProtocolOf(message.Version),
            SecureConnection = string.Equals(response.FinalUrl.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase),
            RenderBlocking = RenderBlocking,
            ServerTiming = message.Headers.TryGetValues("Server-Timing", out var serverTiming) ? string.Join(", ", serverTiming) : null,
        };
    }

    /// <summary>
    /// Fetch's TAO check, which every hop of the fetch has to pass: a response of the document's own
    /// origin passes, and so does one whose <c>Timing-Allow-Origin</c> lists <c>*</c> or the document's
    /// origin. Only the final response's headers are seen, so a redirect from another origin fails.
    /// </summary>
    internal static bool PassesTimingAllowCheck(TransportResponse response, DocumentRequestContext client)
    {
        var origin = client.Origin;
        var hops = response.UrlList;
        for (var i = 0; i + 1 < hops.Count; i++)
        {
            if (!Origin.FromUrl(hops[i]).IsSameOrigin(origin))
                return false;
        }

        if (response.Message.Headers.TryGetValues("Timing-Allow-Origin", out var values))
        {
            var serialized = origin.ToString();
            if (values
                .SelectMany(static value => value.Split(','))
                .Select(static value => value.Trim())
                .Any(value => value == "*" || string.Equals(value, serialized, StringComparison.Ordinal)))
                return true;
        }

        return Origin.FromUrl(response.FinalUrl).IsSameOrigin(origin);
    }

    /// <summary>The ALPN identifier for an HTTP version, as <c>nextHopProtocol</c> names it.</summary>
    internal static string ProtocolOf(Version version) => version.Major switch
    {
        3 => "h3",
        2 => "h2",
        1 when version.Minor == 0 => "http/1.0",
        _ => "http/1.1",
    };

    /// <summary>
    /// Whether a parser-inserted resource named <paramref name="reference"/> in <paramref name="html"/>
    /// is in the document's head: before its <c>&lt;body&gt;</c>, which is where a scan that reports no
    /// positions finds the reference. A document without one has nothing that is not its head yet.
    /// </summary>
    internal static bool IsInHead(string html, string reference)
    {
        var body = html.IndexOf("<body", StringComparison.OrdinalIgnoreCase);
        var at = html.IndexOf(reference, StringComparison.Ordinal);
        return body < 0 || at >= 0 && at < body;
    }

    /// <summary>The moment, on <c>performance.now()</c>'s clock, a loader takes as a fetch's milestone.</summary>
    internal static long Now() => Stopwatch.GetTimestamp();
}
