using System.Net;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The Long Tasks API: an entry for every task longer than 50 milliseconds, for the observers of the
/// documents it is reported to. Shapes, names and attribution are Chromium's, measured.
/// </summary>
/// <remarks>
/// The bridge reported none: <c>longtask</c> was not a type an observer could ask for, and reCAPTCHA
/// observes them. The tasks here busy-wait on <c>performance.now()</c>, so they are long on any machine,
/// and only the entry that covers the wait is looked at -- this engine may find another task long too.
/// </remarks>
public class LongTaskTests
{
    private const string Out = "<div id=\"out\"></div>";

    private const string Helpers =
        "function done(v) { document.getElementById('out').textContent = String(v); }" +
        "function busy(ms) { var t = performance.now(); while (performance.now() - t < ms) {} }" +
        // The entries that cover the busy-wait begun at t0, whatever else this engine found long.
        "var t0 = -1;" +
        "function busyFrom(ms) { t0 = performance.now(); busy(ms); }" +
        "function covering(entries, ms) { return entries.filter(function (e) { return e.startTime <= t0 && e.startTime + e.duration >= t0 + ms - 1; }); }";

    private static string Run(string script, string body = "")
    {
        var rendered = new ScriptEngine().Execute(
            [Helpers, script],
            $"<!DOCTYPE html><html><head></head><body>{Out}{body}</body></html>",
            "https://example.test/page");
        Assert.NotNull(rendered);
        return PageProbe.OutOf(rendered!, decode: true);
    }

    /// <summary>
    /// A timer that runs for longer than 50 ms is a long task of its document's own: <c>self</c>, a whole
    /// number of milliseconds at least as long as it ran, attributed to the window, whose container is
    /// named by nothing. It is not in the performance timeline.
    /// </summary>
    [Fact]
    public void ALongTimerIsItsDocumentsOwnLongTask()
    {
        Assert.Equal(
            "1 | self,longtask,true,true,true | [object PerformanceLongTaskTiming],true" +
            " | [{\"name\":\"unknown\",\"entryType\":\"taskattribution\",\"startTime\":0,\"duration\":0,\"containerType\":\"window\"," +
            "\"containerSrc\":\"\",\"containerId\":\"\",\"containerName\":\"\"}]" +
            " | name,entryType,startTime,duration,attribution | 0,true",
            Run(
                "var seen = [];" +
                "new PerformanceObserver(function (list) { seen = seen.concat(list.getEntries()); }).observe({ type: 'longtask' });" +
                "setTimeout(function () { busyFrom(120); }, 0);" +
                "setTimeout(function () {" +
                "  var long = covering(seen, 120), e = long[0];" +
                "  done([long.length, [e.name, e.entryType, Number.isInteger(e.duration), e.startTime > 0, e.duration < 5000].join()," +
                "    [Object.prototype.toString.call(e), e instanceof PerformanceEntry].join(), JSON.stringify(e.attribution)," +
                "    Object.keys(e.toJSON()).join(), [performance.getEntriesByType('longtask').length," +
                "    PerformanceObserver.supportedEntryTypes.indexOf('longtask') >= 0].join()].join(' | ')); }, 50);"));
    }

    /// <summary>
    /// A page script is a task too, and an observer that asks for the buffered long tasks is handed the
    /// ones that came before it; one that asks with <c>entryTypes</c> is not.
    /// </summary>
    [Fact]
    public void ABufferedObserverIsHandedEarlierLongTasks()
    {
        var rendered = new ScriptEngine().Execute(
            [Helpers, "busyFrom(120);",
             "var buffered = [], unbuffered = [];" +
             "new PerformanceObserver(function (list) { buffered = buffered.concat(list.getEntries()); }).observe({ type: 'longtask', buffered: true });" +
             "new PerformanceObserver(function (list) { unbuffered = unbuffered.concat(list.getEntries()); }).observe({ entryTypes: ['longtask'] });" +
             "setTimeout(function () { done([covering(buffered, 120).length, covering(unbuffered, 120).length].join()); }, 10);"],
            $"<!DOCTYPE html><html><head></head><body>{Out}</body></html>",
            "https://example.test/page");

        Assert.Equal("1,0", PageProbe.OutOf(rendered!, decode: true));
    }

    /// <summary>
    /// A frame's long task is the frame's own, and its parent of the same origin is told of it as
    /// <c>same-origin-descendant</c>, attributed to the frame element: its type, <c>src</c>, <c>id</c> and
    /// <c>name</c>. A frame of another origin keeps its long tasks to itself.
    /// </summary>
    /// <remarks>
    /// Loading a frame is the frame's work too, and long in this engine, so the busy task is picked out
    /// by when it ran: the entry that covers it.
    /// </remarks>
    [Fact]
    public void AFramesLongTaskIsItsOwnAndItsSameOriginParents()
    {
        // Every document's script shares the one global here, so the frame keeps its own in a function.
        // It tells the page when its busy task began and what it was told of it.
        const string frameScript =
            "<script>(function () { var seen = [], t0 = 0;" +
            "new PerformanceObserver(function (list) { seen = seen.concat(list.getEntries()); }).observe({ type: 'longtask' });" +
            "setTimeout(function () { t0 = performance.now(); while (performance.now() - t0 < 120) {} }, 0);" +
            "setTimeout(function () { var e = seen.filter(function (e) { return e.startTime <= t0 && e.startTime + e.duration >= t0 + 119; });" +
            "  top.postMessage(t0 + '|' + e.map(function (x) { return x.name + ' ' + x.attribution[0].containerType; }).join(';'), '*'); }, 50); })();</script>";
        var same = $"<iframe id=\"f\" name=\"n\" srcdoc=\"{WebUtility.HtmlEncode(frameScript)}\"></iframe>";
        var other = $"<iframe id=\"o\" src=\"data:text/html,{Uri.EscapeDataString(frameScript)}\"></iframe>";

        Assert.Equal(
            "frame: self window | other: self window | page: same-origin-descendant iframe  f n",
            Run(
                "var seen = [], heard = {};" +
                "new PerformanceObserver(function (list) { seen = seen.concat(list.getEntries()); }).observe({ type: 'longtask' });" +
                "var a = document.getElementById('f').contentWindow, b = document.getElementById('o').contentWindow;" +
                "window.addEventListener('message', function (m) { heard[m.source === a ? 'frame' : 'other'] = m.data.split('|'); });" +
                "setTimeout(function () {" +
                "  var t0 = +heard.frame[0], page = seen.filter(function (e) { return e.startTime <= t0 && e.startTime + e.duration >= t0 + 119; })" +
                "    .map(function (e) { var a = e.attribution[0]; return [e.name, a.containerType, a.containerSrc, a.containerId, a.containerName].join(' '); });" +
                "  done(['frame: ' + heard.frame[1], 'other: ' + heard.other[1], 'page: ' + page.join(';')].join(' | ')); }, 200);",
                same + other));
    }

    /// <summary>
    /// The entry interfaces are Chromium's: their members on the prototype, not constructible, and the
    /// attribution a new frozen array of the same objects at every read.
    /// </summary>
    [Fact]
    public void TheEntryInterfacesAreChromiums()
    {
        Assert.Equal(
            "attribution,constructor,toJSON | constructor,containerId,containerName,containerSrc,containerType,toJSON" +
            " | Failed to construct 'PerformanceLongTaskTiming': Illegal constructor" +
            " | Failed to construct 'TaskAttributionTiming': Illegal constructor" +
            " | true,false,true,[object TaskAttributionTiming],true",
            Run(
                "var seen = [];" +
                "new PerformanceObserver(function (list) { seen = seen.concat(list.getEntries()); }).observe({ type: 'longtask' });" +
                "setTimeout(function () { busyFrom(120); }, 0);" +
                "setTimeout(function () {" +
                "  var e = covering(seen, 120)[0], a = e.attribution, errors = [];" +
                "  try { new PerformanceLongTaskTiming(); } catch (x) { errors.push(x.message); }" +
                "  try { new TaskAttributionTiming(); } catch (x) { errors.push(x.message); }" +
                "  done([Object.getOwnPropertyNames(PerformanceLongTaskTiming.prototype).sort().join()," +
                "    Object.getOwnPropertyNames(TaskAttributionTiming.prototype).sort().join()].concat(errors)" +
                "    .concat([[Object.isFrozen(a), a === e.attribution, a[0] === e.attribution[0], Object.prototype.toString.call(a[0])," +
                "      a[0] instanceof PerformanceEntry].join()]).join(' | ')); }, 50);"));
    }
}
