using System;
using System.Diagnostics;

namespace Broiler.HtmlBridge.Net;

/// <summary>
/// What the host observed while fetching the top-level document, and the instant the navigation
/// started — the two things a <c>PerformanceNavigationTiming</c> entry's network half is made of
/// (Navigation Timing Level 2 §4, Resource Timing §4.1).
/// </summary>
/// <remarks>
/// <para>
/// It exists because <b>nothing below the CLI can measure this</b>. The document is retrieved by the
/// capture host and handed to <c>DomBridge</c> as a string, so by the time the bridge — let alone the
/// <c>performance</c> object — exists, the fetch is over. Those marks therefore reported <c>0</c>,
/// which in Navigation Timing means "not observed" rather than "instantaneous"; the RUM arithmetic
/// built on them yielded a number, but not a measurement.
/// </para>
/// <para>
/// <b>The time origin is the point of this type, not a detail of it.</b> A mark is a
/// <c>DOMHighResTimeStamp</c> — milliseconds since the document's time origin — and the origin is the
/// navigation's start (HR-Time §5). The bridge used to stamp its origin when it built the
/// <c>performance</c> object, which is already <em>after</em> the fetch, so every real network mark
/// would have been negative and clamped to the specification's floor of 0. Handing the host's origin
/// across is what makes the phases expressible at all; a bridge given one measures
/// <c>performance.now()</c> and its lifecycle marks from the same instant, so a page comparing a
/// network mark with a <c>now()</c> reading gets two points on one timeline.
/// </para>
/// <para>
/// Marks are idempotent — the first call for a phase wins. A connection can be established once and
/// reused, and a redirect chain issues more than one request inside a single <c>SendAsync</c>, so a
/// later repeat of a phase must not overwrite the navigation's own.
/// </para>
/// <para>
/// A phase that was never marked resolves to the previous phase rather than to <c>0</c>, which is
/// what the specification asks for when a fetch did not perform it: a <c>file:</c> document does no
/// DNS lookup and opens no connection, so its <c>domainLookupStart</c> … <c>connectEnd</c> all equal
/// <c>fetchStart</c>. <see cref="SecureConnectionStart"/> is the documented exception: it stays
/// <c>0</c> when no TLS handshake took place.
/// </para>
/// </remarks>
public sealed class DocumentFetchTiming
{
    private readonly long _monotonicOrigin;

    private double? _fetchStart;
    private double? _domainLookupStart;
    private double? _domainLookupEnd;
    private double? _connectStart;
    private double? _secureConnectionStart;
    private double? _connectEnd;
    private double? _requestStart;
    private double? _responseStart;
    private double? _responseEnd;

    private DocumentFetchTiming(long monotonicOrigin, long unixTimeOriginMs)
    {
        _monotonicOrigin = monotonicOrigin;
        UnixTimeOriginMs = unixTimeOriginMs;
    }

    /// <summary>
    /// Stamps the navigation's start — the document's time origin — and returns the recorder the
    /// host marks each phase on as it reaches it. Call this immediately before the fetch begins:
    /// everything the entry reports is measured from here, so an origin taken late shortens every
    /// phase by the same amount.
    /// </summary>
    public static DocumentFetchTiming StartNavigation() =>
        new(Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    /// <summary>
    /// The <see cref="Stopwatch.GetTimestamp"/> value taken at the navigation start, for a consumer
    /// that measures its own marks from the same origin.
    /// </summary>
    public long MonotonicOrigin => _monotonicOrigin;

    /// <summary>
    /// The wall-clock estimate of the same instant, in Unix milliseconds — what
    /// <c>performance.timeOrigin</c> reports (HR-Time §5).
    /// </summary>
    public long UnixTimeOriginMs { get; }

    /// <summary>Milliseconds since the navigation start, as a <c>DOMHighResTimeStamp</c>.</summary>
    private double Now() => Stopwatch.GetElapsedTime(_monotonicOrigin).TotalMilliseconds;

    /// <summary>The host is about to begin fetching the document.</summary>
    public void MarkFetchStart() => _fetchStart ??= Now();

    /// <summary>Brackets the DNS lookup for the document's host.</summary>
    public void MarkDomainLookupStart() => _domainLookupStart ??= Now();

    /// <inheritdoc cref="MarkDomainLookupStart"/>
    public void MarkDomainLookupEnd() => _domainLookupEnd ??= Now();

    /// <summary>The host is about to open a transport connection.</summary>
    public void MarkConnectStart() => _connectStart ??= Now();

    /// <summary>
    /// The transport is up and the TLS handshake is about to begin. Never called for a scheme that
    /// negotiates no TLS, which is what leaves <see cref="SecureConnectionStart"/> at the
    /// specification's <c>0</c> for one.
    /// </summary>
    public void MarkSecureConnectionStart() => _secureConnectionStart ??= Now();

    /// <summary>The connection is usable — after the TLS handshake where there is one.</summary>
    public void MarkConnectEnd() => _connectEnd ??= Now();

    /// <summary>The request is about to go out.</summary>
    public void MarkRequestStart() => _requestStart ??= Now();

    /// <summary>The first byte of the response — its headers — has arrived.</summary>
    public void MarkResponseStart() => _responseStart ??= Now();

    /// <summary>The last byte of the response body has arrived.</summary>
    public void MarkResponseEnd() => _responseEnd ??= Now();

    /// <summary>
    /// Records Resource Timing's body-size trio (§4.1). <paramref name="transferSize"/> is the
    /// payload plus the response header fields as they crossed the wire; it is <c>0</c> for a
    /// resource that crossed no network. <paramref name="encodedBodySize"/> is the payload before
    /// any content codings are removed and <paramref name="decodedBodySize"/> after — equal when no
    /// content coding was applied.
    /// </summary>
    public void RecordBodySizes(long transferSize, long encodedBodySize, long decodedBodySize)
    {
        TransferSize = transferSize;
        EncodedBodySize = encodedBodySize;
        DecodedBodySize = decodedBodySize;
    }

    /// <summary>Whether the fetch this timing describes ran to completion, so its marks describe a
    /// response rather than an abandoned attempt.</summary>
    public bool IsComplete => _responseEnd.HasValue;

    /// <inheritdoc cref="MarkFetchStart"/>
    public double FetchStart => _fetchStart ?? 0;

    /// <inheritdoc cref="MarkDomainLookupStart"/>
    public double DomainLookupStart => _domainLookupStart ?? FetchStart;

    /// <inheritdoc cref="MarkDomainLookupEnd"/>
    public double DomainLookupEnd => _domainLookupEnd ?? DomainLookupStart;

    /// <inheritdoc cref="MarkConnectStart"/>
    public double ConnectStart => _connectStart ?? DomainLookupEnd;

    /// <inheritdoc cref="MarkSecureConnectionStart"/>
    public double SecureConnectionStart => _secureConnectionStart ?? 0;

    /// <inheritdoc cref="MarkConnectEnd"/>
    public double ConnectEnd => _connectEnd ?? ConnectStart;

    /// <inheritdoc cref="MarkRequestStart"/>
    public double RequestStart => _requestStart ?? ConnectEnd;

    /// <inheritdoc cref="MarkResponseStart"/>
    public double ResponseStart => _responseStart ?? RequestStart;

    /// <inheritdoc cref="MarkResponseEnd"/>
    public double ResponseEnd => _responseEnd ?? ResponseStart;

    /// <inheritdoc cref="RecordBodySizes"/>
    public long TransferSize { get; private set; }

    /// <inheritdoc cref="RecordBodySizes"/>
    public long EncodedBodySize { get; private set; }

    /// <inheritdoc cref="RecordBodySizes"/>
    public long DecodedBodySize { get; private set; }
}
