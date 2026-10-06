using System.Runtime.CompilerServices;
using Broiler.JSeal;

namespace Broiler.HtmlBridge;

/// <summary>
/// The Performance Timeline and User Timing: <c>performance.mark</c>/<c>measure</c>, the entry getters
/// over what a document recorded, and <c>PerformanceObserver</c>. The algorithms are host script
/// (<c>Polyfills/performance-timeline.js</c>), as the specifications write them; this file gives that
/// script what only the host knows.
/// </summary>
/// <remarks>
/// <para>
/// <b>What was there.</b> <c>mark</c> and <c>measure</c> returned <c>undefined</c> and recorded nothing,
/// the getters answered from the navigation entry alone, and <c>PerformanceObserver</c> accepted
/// <c>observe()</c> and never called back. A page that marks its own progress and reads it back, or
/// observes it, found nothing.
/// </para>
/// <para>
/// <b>One timeline per document.</b> Every document's script runs on the one global, so the
/// <c>performance</c> object is shared, and its methods ask which document's script is running
/// (<see cref="CurrentScriptDocumentContext"/>), as <c>location</c> does: a frame's marks, measures and
/// observers are its own, and a frame of another origin does not see the page's. A frame's navigation
/// entry is its own too -- its URL, with the timings this bridge does not measure for a frame at the
/// specification's "not observed" 0 -- where it used to read the page's. <c>performance.now()</c> stays
/// the page's clock.
/// </para>
/// </remarks>
public sealed partial class DomBridge
{
    // A number for each document's request context, which the script keys its timelines on.
    private readonly ConditionalWeakTable<Broiler.Net.Http.DocumentRequestContext, object> _timelineKeys = new();
    private int _nextTimelineKey;

    // Each frame document's navigation entry, made the first time its script asks for one.
    private readonly ConditionalWeakTable<Broiler.Net.Http.DocumentRequestContext, StrongBox<JsValue>> _frameNavigationEntries = new();

    private JsValue _pageNavigationEntry;
    private long _performanceMonotonicOrigin;

    /// <summary>
    /// Installs the timeline on <paramref name="performance"/> and its interface objects on the global,
    /// with <paramref name="pageNavigationEntry"/> as the page's navigation entry.
    /// </summary>
    private void InstallPerformanceTimeline(JsValue performance, JsValue pageNavigationEntry, long monotonicOrigin)
    {
        var realm = Realm;
        _pageNavigationEntry = pageNavigationEntry;
        _performanceMonotonicOrigin = monotonicOrigin;

        var hooks = realm.NewObject();
        realm.DefineValue(hooks, "performance", performance);
        realm.DefineMethod(hooks, "documentKey", 0, (in _) => JsValue.Number(TimelineKeyOf(CurrentScriptDocumentContext())));
        realm.DefineMethod(hooks, "topDocumentKey", 0, (in _) => JsValue.Number(TimelineKeyOf(TopDocumentContext)));
        realm.DefineMethod(hooks, "navigationEntry", 0, (in _) => NavigationEntryForCurrentScript());
        realm.DefineMethod(hooks, "queueTask", 1, (in call) =>
        {
            var task = call[0];
            var taskRealm = call.Realm;
            _eventLoop.QueueTask(() => taskRealm.Invoke(task, JsValue.Undefined, []));
            return JsValue.Undefined;
        });
        realm.DefineMethod(hooks, "bind", 1, (in call) => Dom.Features.TimerBinding.BindToRegisteringContext(call.Realm, _windowContext, call[0]));
        realm.DefineMethod(hooks, "clone", 2, (in call) => CloneEntryDetail(call.Realm, call[0], call.Realm.ToJsString(call[1])));
        realm.DefineMethod(hooks, "takeResources", 0, (in call) => TakeResourceTimings(call.Realm));

        realm.SetProperty(realm.Global, "__broilerPerformanceTimeline", hooks);
        realm.EvaluateHostScript(PolyfillAssets.PerformanceTimeline, "polyfill:performance-timeline");

        // The page's navigation entry reaches the observers waiting for one when its load event ends.
        var loadEventEnded = realm.GetProperty(hooks, "loadEventEnded");
        if (loadEventEnded.IsFunction && _navigationTiming is { } timing)
            timing.LoadEventEnded += () => realm.Invoke(loadEventEnded, JsValue.Undefined, []);

        // The fetches whose records have arrived become entries when a task hands them over
        // (DomBridge/ResourceTimings.cs).
        _resourcesArrived = realm.GetProperty(hooks, "resourcesArrived");

        // Every task the event loop runs is timed, and a long one reported (DomBridge/LongTasks.cs).
        _longTaskEnded = realm.GetProperty(hooks, "longTaskEnded");
        _eventLoop.TaskMonitor = this;
        _windowContext.WindowEntered = TaskWindowEntered;
        _windowContext.WindowExited = TaskWindowExited;
    }

    private int TimelineKeyOf(Broiler.Net.Http.DocumentRequestContext document) =>
        (int)_timelineKeys.GetValue(document, _ => Interlocked.Increment(ref _nextTimelineKey));

    /// <summary>
    /// The navigation entry of the document whose script is running: the page's, or the frame's own.
    /// </summary>
    private JsValue NavigationEntryForCurrentScript()
    {
        if (_windowContext.ResolveCurrentSubWindow() is not { } frameWindow)
            return _pageNavigationEntry;

        var realm = Realm;
        var box = _frameNavigationEntries.GetValue(CurrentScriptDocumentContext(), _ =>
        {
            // The frame's URL as its Location shows it, which is what a frame's entry is named by.
            var href = realm.ToJsString(realm.GetProperty(realm.GetProperty(frameWindow, "location"), "href"));
            var protocol = Uri.TryCreate(href, UriKind.Absolute, out var url) ? url.Scheme + ":" : string.Empty;
            return new StrongBox<JsValue>(Dom.Features.NavigationTimingBinding.BuildEntry(
                realm, href, protocol, new Dom.Features.NavigationTimingState(_performanceMonotonicOrigin), fetchTiming: null));
        });
        return box.Value;
    }

    /// <summary>
    /// A mark's or a measure's <c>detail</c>: its structured clone, or the <c>DataCloneError</c> it is
    /// refused with. On an engine that cannot clone, the value itself.
    /// </summary>
    private static JsValue CloneEntryDetail(IJsRealm realm, JsValue detail, string messagePrefix)
    {
        if (!Dom.Features.WorkerTransfer.CanStructuredClone(realm))
            return detail;

        try
        {
            return realm.Clone(detail, []);
        }
        catch (JsEngineException)
        {
            var what = detail.IsFunction ? realm.ToJsString(detail) : "The object";
            throw realm.DomError("DataCloneError", messagePrefix + what + " could not be cloned.");
        }
    }
}
