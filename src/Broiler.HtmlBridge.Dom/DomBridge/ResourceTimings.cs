using System.Collections.Concurrent;
using Broiler.JSeal;

namespace Broiler.HtmlBridge;

/// <summary>
/// Resource Timing: a <c>PerformanceResourceTiming</c> entry in a document's timeline for each fetch
/// the document made -- its scripts, its style sheets and their imports, its frames, its workers' scripts,
/// its <c>fetch()</c>, <c>XMLHttpRequest</c> and <c>sendBeacon</c> requests.
/// </summary>
/// <remarks>
/// <para>
/// <b>What was there.</b> No entry was ever made: <c>getEntriesByType('resource')</c> answered an empty
/// list, and a page that looks its own scripts up there -- reCAPTCHA does -- found nothing, which no
/// browser answers.
/// </para>
/// <para>
/// <b>How a fetch reaches the timeline.</b> The loaders time each fetch they are handed a
/// <see cref="ResourceTimingRequest"/> for and give its <see cref="ResourceTimingRecord"/> to this
/// bridge's sink, on whichever thread finished the fetch. The sink queues it, and a task on the event
/// loop hands what has arrived to the timeline script (<c>Polyfills/performance-timeline.js</c>), as
/// does any read of the timeline before that task has run. The scripts the host fetched before the
/// bridge existed arrive through <see cref="AddDocumentResourceTimings"/>.
/// </para>
/// <para>
/// <b>What an entry shows is Chromium's, measured.</b> A fetch that failed the timing-allow check shows
/// its start and its end and nothing between; one whose response is not the document's to read shows
/// no status or type. Times are on the page's clock; a fetch made before the time origin -- by a host
/// that did not hand the navigation's start across -- is clamped to it, the earliest instant the
/// timeline can express.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    // Resource Timing's fixed estimate of a response's header bytes, which transferSize adds to the
    // body's (Resource Timing §4.3, "transferSize").
    private const int ResourceTimingHeaderBytes = 300;

    private readonly ConcurrentQueue<ResourceTimingRecord> _arrivedResourceTimings = new();
    private int _resourceTimingDeliveryQueued;
    private IResourceTimingSink? _resourceTimingSink;
    private JsValue _resourcesArrived;

    // The style sheets each timeline has an entry for. The bridge fetches a sheet for more than one of
    // its own purposes -- the cascade, the link's load event, a computed-style scope's imports -- where
    // a browser fetches it once and serves the rest from its memory cache; only the first is the page's.
    private readonly HashSet<(int Timeline, string Url, string InitiatorType)> _recordedStyleSheets = [];

    /// <summary>Where this bridge's loaders give the records of the fetches they timed.</summary>
    internal IResourceTimingSink ResourceTimingSink => _resourceTimingSink ??= new BridgeResourceTimingSink(this);

    private sealed class BridgeResourceTimingSink(DomBridge bridge) : IResourceTimingSink
    {
        public void Record(ResourceTimingRecord record) => bridge.ResourceTimingArrived(record);
    }

    /// <summary>
    /// Adds the records of fetches the host made for this document before attaching it -- the scripts it
    /// extracted and fetched -- to the top document's timeline.
    /// </summary>
    internal void AddDocumentResourceTimings(IEnumerable<ResourceTimingRecord> records)
    {
        // The host's request context named the same document; the timeline is this bridge's.
        var top = TopDocumentContext;
        foreach (var record in records)
            ResourceTimingArrived(record with { Client = top });
    }

    private void ResourceTimingArrived(ResourceTimingRecord record)
    {
        if (_disposed)
            return;

        _arrivedResourceTimings.Enqueue(record);
        if (Interlocked.Exchange(ref _resourceTimingDeliveryQueued, 1) == 0)
            _eventLoop.QueueTask(DeliverResourceTimings);
    }

    private void DeliverResourceTimings()
    {
        Volatile.Write(ref _resourceTimingDeliveryQueued, 0);
        if (_realm is { } realm && _resourcesArrived.IsFunction)
            realm.Invoke(_resourcesArrived, JsValue.Undefined, []);
    }

    /// <summary>
    /// Re-queues the delivery a cleared event loop dropped. What waits for it stays: a new document's
    /// speculative fetches start before the old document's session is cleared, and a late record of
    /// the old document's only reaches the old document's timeline, which nothing reads any more.
    /// </summary>
    private void ResumeResourceTimingDelivery()
    {
        _recordedStyleSheets.Clear();
        Volatile.Write(ref _resourceTimingDeliveryQueued, 0);
        if (!_disposed && !_arrivedResourceTimings.IsEmpty && Interlocked.Exchange(ref _resourceTimingDeliveryQueued, 1) == 0)
            _eventLoop.QueueTask(DeliverResourceTimings);
    }

    /// <summary>
    /// What has arrived since the timeline last asked, as the timeline script makes its entries of it:
    /// for each record, the timeline it belongs to and the values its entry shows.
    /// </summary>
    private JsValue TakeResourceTimings(IJsRealm realm)
    {
        var entries = new List<JsValue>();
        while (_arrivedResourceTimings.TryDequeue(out var record))
        {
            var timeline = TimelineKeyOf(record.Client);
            if (record.InitiatorType is "link" or "css" &&
                !_recordedStyleSheets.Add((timeline, record.Name.AbsoluteUri, record.InitiatorType)))
                continue;

            entries.Add(DescribeResourceTiming(realm, timeline, record));
        }

        return realm.NewArray([.. entries]);
    }

    /// <summary>
    /// A record's entry, as Chromium shows it: the times relative to the time origin; without the
    /// timing-allow check, only its start and its end; without the response being the document's to
    /// read, no status, type or encoding.
    /// </summary>
    private JsValue DescribeResourceTiming(IJsRealm realm, int timeline, ResourceTimingRecord record)
    {
        var start = RelativeToTimeOrigin(record.FetchStart);
        var data = realm.NewObject();

        void Set(string name, JsValue value) => realm.DefineValue(data, name, value);
        void Time(string name, double value) => Set(name, JsValue.Number(value));

        Set("key", JsValue.Number(timeline));
        Set("name", JsValue.String(record.Name.AbsoluteUri));
        Set("initiatorType", JsValue.String(record.InitiatorType));
        Set("renderBlockingStatus", JsValue.String(record.RenderBlocking ? "blocking" : "non-blocking"));
        Time("startTime", start);
        Time("responseEnd", Math.Max(start, RelativeToTimeOrigin(record.ResponseEnd)));

        var details = record.ResponseDetailsAllowed;
        Set("responseStatus", JsValue.Number(details ? record.ResponseStatus : 0));
        Set("contentType", JsValue.String(details ? record.ContentType : string.Empty));
        Set("contentEncoding", JsValue.String(details ? record.ContentEncoding : string.Empty));

        if (!record.TimingAllowed)
        {
            Time("fetchStart", start);
            return data;
        }

        // The connection's phases are not observed (ResourceTimingRecord): they are reported as a
        // reused connection's, at the fetch's own start -- after its redirects, when it had any.
        var redirected = record.RedirectEnd != 0;
        var fetchStart = redirected ? Math.Max(start, RelativeToTimeOrigin(record.RedirectEnd)) : start;
        var responseStart = Math.Max(fetchStart, RelativeToTimeOrigin(record.ResponseStart));
        Time("redirectStart", redirected ? start : 0);
        Time("redirectEnd", redirected ? fetchStart : 0);
        Time("fetchStart", fetchStart);
        foreach (var phase in (ReadOnlySpan<string>)["domainLookupStart", "domainLookupEnd", "connectStart", "connectEnd", "requestStart"])
            Time(phase, fetchStart);
        Time("secureConnectionStart", record.SecureConnection ? fetchStart : 0);
        Time("responseStart", responseStart);
        Time("finalResponseHeadersStart", responseStart);
        Set("nextHopProtocol", JsValue.String(record.NextHopProtocol));
        Set("transferSize", JsValue.Number(record.EncodedBodySize + ResourceTimingHeaderBytes));
        Set("encodedBodySize", JsValue.Number(record.EncodedBodySize));
        Set("decodedBodySize", JsValue.Number(record.DecodedBodySize));
        Set("serverTiming", JsValue.String(record.ServerTiming ?? string.Empty));
        return data;
    }

    /// <summary>A <see cref="System.Diagnostics.Stopwatch"/> timestamp as milliseconds since the time origin, not before it.</summary>
    private double RelativeToTimeOrigin(long timestamp) =>
        Math.Max(0, System.Diagnostics.Stopwatch.GetElapsedTime(_performanceMonotonicOrigin, timestamp).TotalMilliseconds);

    /// <summary>
    /// The request a frame's document is fetched with, as <paramref name="container"/>'s document's
    /// Resource Timing records it: named by the container, <c>iframe</c>, <c>frame</c>, <c>object</c> or <c>embed</c>.
    /// </summary>
    private ResourceTimingRequest FrameResourceTiming(Broiler.Dom.DomElement container) =>
        new(container.TagName?.ToLowerInvariant() is { Length: > 0 } tag ? tag : "iframe", DocumentContextFor(container), ResourceTimingSink);
}
