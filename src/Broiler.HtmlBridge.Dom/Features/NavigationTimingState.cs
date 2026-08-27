using System.Diagnostics;

namespace Broiler.HtmlBridge.Dom.Features;

/// <summary>
/// The document-lifecycle marks a <c>PerformanceNavigationTiming</c> entry reports, stamped by the
/// bridge's load sequence as each moment actually happens.
/// </summary>
/// <remarks>
/// <para>
/// This exists because of an ordering problem: the navigation entry is built while the
/// <c>performance</c> object is registered, which is <em>before</em> the document is interactive and
/// long before <c>load</c> fires. The entry therefore cannot hold these as values — it reads them
/// through this holder, which the load path fills in as it goes.
/// </para>
/// <para>
/// Every mark here is <b>measured</b>, from the same monotonic clock <c>performance.now()</c> uses
/// and against the same time origin, so a page comparing a mark with a <c>performance.now()</c>
/// reading gets two points on one timeline. A mark that has not been reached yet reads <c>0</c>,
/// which is what Navigation Timing specifies for a moment that has not occurred — so
/// <c>domComplete</c> read from a script running during parse is <c>0</c> rather than a guess, and
/// becomes a real value once the document completes.
/// </para>
/// </remarks>
internal sealed class NavigationTimingState
{
    private readonly long _monotonicOrigin;

    /// <param name="monotonicOrigin">
    /// The <see cref="Stopwatch.GetTimestamp"/> value captured at the performance time origin — the
    /// same one <c>performance.now()</c> measures from, so the two agree.
    /// </param>
    public NavigationTimingState(long monotonicOrigin) => _monotonicOrigin = monotonicOrigin;

    /// <summary>Milliseconds since the time origin, as a <c>DOMHighResTimeStamp</c>.</summary>
    private double Now() => Stopwatch.GetElapsedTime(_monotonicOrigin).TotalMilliseconds;

    public double DomInteractive { get; private set; }
    public double DomContentLoadedEventStart { get; private set; }
    public double DomContentLoadedEventEnd { get; private set; }
    public double DomComplete { get; private set; }
    public double LoadEventStart { get; private set; }
    public double LoadEventEnd { get; private set; }

    /// <summary>The document has finished parsing — <c>readyState</c> becomes "interactive".</summary>
    public void MarkDomInteractive() => DomInteractive = Now();

    /// <summary>Brackets the <c>DOMContentLoaded</c> dispatch.</summary>
    public void MarkDomContentLoadedStart() => DomContentLoadedEventStart = Now();

    public void MarkDomContentLoadedEnd() => DomContentLoadedEventEnd = Now();

    /// <summary>Sub-resources are accounted for — <c>readyState</c> becomes "complete".</summary>
    public void MarkDomComplete() => DomComplete = Now();

    /// <summary>Brackets the <c>load</c> dispatch.</summary>
    public void MarkLoadEventStart() => LoadEventStart = Now();

    public void MarkLoadEventEnd() => LoadEventEnd = Now();
}
