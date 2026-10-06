using System.Net;
using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// User Timing -- <c>performance.mark</c>, <c>measure</c>, <c>clearMarks</c>, <c>clearMeasures</c> -- the
/// entry getters over what a document recorded, and <c>PerformanceObserver</c>. Every expectation is
/// Chromium's, measured with the same scripts.
/// </summary>
/// <remarks>
/// <c>mark</c> and <c>measure</c> returned <c>undefined</c> and recorded nothing, the getters answered
/// with the navigation entry alone, and an observer never heard of anything.
/// </remarks>
public class PerformanceTimelineTests
{
    private const string Url = "https://example.test/page";

    // `done(v)` writes v into #out; `fail(f)` is what f returned, or `Name: message` for what it threw;
    // `names(list)` spells entries as `type:name@start+duration`.
    private const string Helpers =
        "function done(v) { document.getElementById('out').textContent = String(v); }" +
        "function fail(f) { try { var v = f(); return 'ok ' + (v && v.entryType ? v.entryType + ':' + v.name : typeof v); }" +
        " catch (e) { return e.name + ': ' + e.message; } }" +
        "function names(list) { return list.map(function (e) { return e.entryType + ':' + e.name + '@' + e.startTime + '+' + e.duration; }).join(' '); }";

    private static string Run(string script, string body = "")
    {
        var rendered = new ScriptEngine().Execute([Helpers, script],
            $"<!DOCTYPE html><html><head></head><body><div id=\"out\"></div>{body}</body></html>", Url);
        Assert.NotNull(rendered);
        return PageProbe.OutOf(rendered!, decode: true);
    }

    /// <summary>The entry interfaces, and the types an observer can be given.</summary>
    [Fact]
    public void TheInterfacesAreThere()
    {
        Assert.Equal(
            "function function function function function function function|longtask,mark,measure,navigation,resource|true|true|function,undefined,true,true",
            Run("""
                var d = Object.getOwnPropertyDescriptor(PerformanceObserver, 'supportedEntryTypes');
                done([[typeof PerformanceEntry, typeof PerformanceMark, typeof PerformanceMeasure, typeof PerformanceObserver,
                    typeof PerformanceObserverEntryList, typeof PerformanceResourceTiming, typeof PerformanceNavigationTiming].join(' '),
                  PerformanceObserver.supportedEntryTypes, Object.isFrozen(PerformanceObserver.supportedEntryTypes),
                  PerformanceObserver.supportedEntryTypes === PerformanceObserver.supportedEntryTypes,
                  [typeof d.get, typeof d.set, d.enumerable, d.configurable]].join('|'));
                """));
    }

    /// <summary>
    /// A mark is a <c>PerformanceMark</c>, recorded and handed back as the same object; its
    /// <c>detail</c> is a clone, the same at every read, and <c>null</c> when none was given.
    /// </summary>
    [Fact]
    public void AMarkIsRecorded()
    {
        Assert.Equal(
            "[object PerformanceMark]|b mark 5.5 0|{\"x\":1}|true,true,true|b mark 5.5 0|true|null|true,true",
            Run("""
                var detail = { x: 1 };
                var b = performance.mark('b', { startTime: 5.5, detail: detail });
                var j = b.toJSON();
                var later = performance.mark('later');
                done([Object.prototype.toString.call(b), [b.name, b.entryType, b.startTime, b.duration].join(' '),
                  JSON.stringify(b.detail), [b.detail !== detail, b.detail === b.detail, b instanceof PerformanceEntry].join(),
                  [j.name, j.entryType, j.startTime, j.duration].join(' '), performance.getEntriesByName('b')[0] === b,
                  String(later.detail), [later.startTime > 0, later.startTime <= performance.now()].join()].join('|'));
                """));
    }

    /// <summary>What <c>mark</c> and <c>new PerformanceMark</c> refuse, with Chromium's messages.</summary>
    [Theory]
    [InlineData("performance.mark()", "TypeError: Failed to execute 'mark' on 'Performance': 1 argument required, but only 0 present.")]
    [InlineData("performance.mark('neg', { startTime: -1 })", "TypeError: Failed to execute 'mark' on 'Performance': 'neg' cannot have a negative start time.")]
    [InlineData("performance.mark('navigationStart')", "SyntaxError: Failed to execute 'mark' on 'Performance': 'navigationStart' is part of the PerformanceTiming interface, and cannot be used as a mark name.")]
    [InlineData("performance.mark('x', 5)", "TypeError: Failed to execute 'mark' on 'Performance': The provided value is not of type 'PerformanceMarkOptions'.")]
    [InlineData("performance.mark('x', { startTime: NaN })", "TypeError: Failed to execute 'mark' on 'Performance': Failed to read the 'startTime' property from 'PerformanceMarkOptions': The provided double value is non-finite.")]
    [InlineData("performance.mark(Symbol('s'))", "TypeError: Failed to execute 'mark' on 'Performance': Cannot convert a Symbol value to a string")]
    [InlineData("performance.mark(42)", "ok mark:42")]
    [InlineData("performance.mark('o', null)", "ok mark:o")]
    [InlineData("performance.mark('s', { startTime: '7' }).startTime", "ok number")]
    [InlineData("new PerformanceMark('n', { startTime: -1 })", "TypeError: Failed to construct 'PerformanceMark': 'n' cannot have a negative start time.")]
    [InlineData("new PerformanceMark('loadEventEnd')", "SyntaxError: Failed to construct 'PerformanceMark': 'loadEventEnd' is part of the PerformanceTiming interface, and cannot be used as a mark name.")]
    [InlineData("new PerformanceMark()", "TypeError: Failed to construct 'PerformanceMark': 1 argument required, but only 0 present.")]
    [InlineData("PerformanceMark('x')", "TypeError: Failed to construct 'PerformanceMark': Please use the 'new' operator, this DOM object constructor cannot be called as a function.")]
    [InlineData("new PerformanceEntry()", "TypeError: Failed to construct 'PerformanceEntry': Illegal constructor")]
    [InlineData("Object.getOwnPropertyDescriptor(PerformanceEntry.prototype, 'name').get.call({})", "TypeError: Illegal invocation")]
    public void MarkRefusesAsChromiumDoes(string expression, string expected) =>
        Assert.Equal(expected, Run($"done(fail(function () {{ return {expression}; }}));"));

    /// <summary>
    /// A detail that cannot be cloned is a <c>DataCloneError</c>; a mark made with <c>new</c> is not
    /// recorded.
    /// </summary>
    [Fact]
    public void AMarkWithAnUncloneableDetailIsRefusedAndAConstructedOneIsNotRecorded()
    {
        Assert.Equal("DataCloneError|true|ctor mark 1 0|0", Run("""
            var error; try { performance.mark('u', { detail: function () {} }); } catch (e) { error = e; }
            var c = new PerformanceMark('ctor', { startTime: 1 });
            done([error && error.name, error && error.message.indexOf("Failed to execute 'mark' on 'Performance': ") === 0,
              [c.name, c.entryType, c.startTime, c.duration].join(' '), performance.getEntriesByName('ctor').length].join('|'));
            """));
    }

    /// <summary>
    /// A measure runs between two marks, a mark and now, or the times its options give; a PerformanceTiming
    /// name is its moment on the navigation entry; the latest mark of a name is the one measured from.
    /// </summary>
    [Fact]
    public void AMeasureSpansWhatItIsGiven()
    {
        Assert.Equal(
            "measure:m2@5.5+6.25 measure:m4@5.5+4.5 [1] measure:m5@2+3 measure:m6@16+4 measure:m7@0+5.5 measure:m9@0+5.5 " +
            "measure:neg@10+-6 measure:m3@5.5+true measure:12@0+true measure:empty@0+true measure:tw@2+true",
            Run("""
                performance.mark('b', { startTime: 5.5 }); performance.mark('a', { startTime: 11.75 });
                performance.mark('twice', { startTime: 1 }); performance.mark('twice', { startTime: 2 });
                var out = [];
                function show(m) { out.push(m.entryType + ':' + m.name + '@' + m.startTime + '+' + m.duration); }
                function toNow(m) { out.push(m.entryType + ':' + m.name + '@' + m.startTime + '+' + (m.duration > 0)); }
                show(performance.measure('m2', 'b', 'a'));
                var m4 = performance.measure('m4', { start: 'b', end: 10, detail: [1] }); show(m4); out.push(JSON.stringify(m4.detail));
                show(performance.measure('m5', { start: 2, duration: 3 }));
                show(performance.measure('m6', { end: 20, duration: 4 }));
                show(performance.measure('m7', undefined, 'b'));
                show(performance.measure('m9', 'navigationStart', 'b'));
                show(performance.measure('neg', { start: 10, end: 4 }));
                toNow(performance.measure('m3', 'b'));
                toNow(performance.measure(12));
                toNow(performance.measure('empty', {}));
                toNow(performance.measure('tw', 'twice'));
                done(out.join(' '));
                """));
    }

    /// <summary>What <c>measure</c> refuses, with Chromium's messages.</summary>
    [Theory]
    [InlineData("performance.measure()", "TypeError: Failed to execute 'measure' on 'Performance': 1 argument required, but only 0 present.")]
    [InlineData("performance.measure('x', 'nope')", "SyntaxError: Failed to execute 'measure' on 'Performance': The mark 'nope' does not exist.")]
    [InlineData("performance.measure('x', 5)", "SyntaxError: Failed to execute 'measure' on 'Performance': The mark '5' does not exist.")]
    [InlineData("performance.measure('x', { start: {} })", "SyntaxError: Failed to execute 'measure' on 'Performance': The mark '[object Object]' does not exist.")]
    [InlineData("performance.measure('x', { start: 1, end: 2, duration: 3 })", "TypeError: Failed to execute 'measure' on 'Performance': If a non-empty PerformanceMeasureOptions object was passed, it must not have all of its 'start', 'duration', and 'end' properties defined")]
    [InlineData("performance.measure('x', { start: 1 }, 'a')", "TypeError: Failed to execute 'measure' on 'Performance': If a non-empty PerformanceMeasureOptions object was passed, |end_mark| must not be passed.")]
    [InlineData("performance.measure('x', { duration: 3 })", "TypeError: Failed to execute 'measure' on 'Performance': If a non-empty PerformanceMeasureOptions object was passed, at least one of its 'start' or 'end' properties must be present.")]
    [InlineData("performance.measure('x', { start: -1 })", "TypeError: Failed to execute 'measure' on 'Performance': 'x' cannot have a negative time stamp.")]
    [InlineData("performance.measure('x', 'loadEventEnd')", "InvalidAccessError: Failed to execute 'measure' on 'Performance': 'loadEventEnd' is empty: either the event hasn't happened yet, or it would provide cross-origin timing information.")]
    [InlineData("performance.getEntriesByType()", "TypeError: Failed to execute 'getEntriesByType' on 'Performance': 1 argument required, but only 0 present.")]
    public void MeasureRefusesAsChromiumDoes(string expression, string expected) =>
        Assert.Equal(expected, Run($"done(fail(function () {{ return {expression}; }}));"));

    /// <summary>
    /// The getters answer from the document's buffer -- its navigation entry, then its marks and measures --
    /// in order of their start, the order they were recorded in where two start together, in a new array
    /// each time; the clears remove marks or measures, all or by name.
    /// </summary>
    [Fact]
    public void TheGettersAnswerFromTheBuffer()
    {
        Assert.Equal(
            "navigation:https://example.test/page measure:m1 measure:m2 mark:b mark:a|mark:b mark:a|0/1|true|2|0 2|0",
            Run("""
                performance.mark('a', { startTime: 9 }); performance.mark('b', { startTime: 4 });
                performance.measure('m1', { start: 0, duration: 1 }); performance.measure('m2', { start: 0, end: 2 });
                var all = performance.getEntries().map(function (e) { return e.entryType + ':' + e.name; }).join(' ');
                var marks = performance.getEntriesByType('mark').map(function (e) { return e.entryType + ':' + e.name; }).join(' ');
                var typed = performance.getEntriesByName('a', 'measure').length + '/' + performance.getEntriesByName('a', 'mark').length;
                var fresh = performance.getEntriesByType('mark') !== performance.getEntriesByType('mark');
                performance.mark('dup'); performance.mark('dup');
                var dups = performance.getEntriesByName('dup').length;
                performance.clearMarks('dup');
                var cleared = performance.getEntriesByName('dup').length + ' ' + performance.getEntriesByType('mark').length;
                performance.clearMeasures();
                done([all, marks, typed, fresh, dups, cleared, performance.getEntriesByType('measure').length].join('|'));
                """));
    }

    /// <summary>
    /// An observer hears of what it observes in one task after the script's microtasks and before a
    /// timer set after the entry, with the observer as <c>this</c>, an entry list in order of start, the
    /// observer, and <c>{ droppedEntriesCount: 0 }</c>.
    /// </summary>
    [Fact]
    public void AnObserverIsCalledBackInATask()
    {
        Assert.Equal(
            "microtask observer obs2 timeout0|true,true,3,{\"droppedEntriesCount\":0},[object PerformanceObserverEntryList]," +
            "measure:o2 mark:o1 mark:t1 mark:later,3,1,true|t1|0",
            Run("""
                var order = [], seen;
                var obs = new PerformanceObserver(function (list, observer, third) {
                  seen = [this === obs, observer === obs, arguments.length, JSON.stringify(third), Object.prototype.toString.call(list),
                    list.getEntries().map(function (e) { return e.entryType + ':' + e.name; }).join(' '),
                    list.getEntriesByType('mark').length, list.getEntriesByName('o1').length, list.getEntries() !== list.getEntries()].join();
                  order.push('observer');
                });
                obs.observe({ entryTypes: ['mark', 'measure'] });
                performance.mark('o1', { startTime: 3 });
                performance.measure('o2', { start: 1, end: 2 });
                Promise.resolve().then(function () { order.push('microtask'); });
                setTimeout(function () { order.push('timeout0'); }, 0);
                var obs2 = new PerformanceObserver(function () { order.push('obs2'); });
                obs2.observe({ type: 'mark' });
                performance.mark('t1', { startTime: 4 });
                var taken = obs2.takeRecords().map(function (e) { return e.name; }).join();
                var again = obs2.takeRecords().length;
                performance.mark('later', { startTime: 5 });
                setTimeout(function () { done([order.join(' '), seen, taken, again].join('|')); }, 50);
                """));
    }

    /// <summary>
    /// <c>buffered</c> hands a <c>type</c> observer what the document recorded before, each time it is
    /// asked -- the navigation entry too, which it hears of again when the load event has ended; an
    /// <c>entryTypes</c> observer gets only what comes after. A type no entry is made of is ignored; two
    /// <c>type</c> calls observe both; <c>disconnect</c> ends it.
    /// </summary>
    [Fact]
    public void BufferedTypesAndDisconnect()
    {
        Assert.Equal(
            "mark:b mark:b mark:a mark:a mark:c mark:t|mark:c mark:t|measure:m mark:t|none|" +
            "navigation:https://example.test/page navigation:https://example.test/page|mark:c mark:t|none",
            Run("""
                performance.mark('a', { startTime: 9 }); performance.mark('b', { startTime: 4 });
                var r = {};
                function record(key) { return function (list) { r[key] = (r[key] ? r[key] + ' ' : '') + list.getEntries().map(function (e) { return e.entryType + ':' + e.name; }).join(' '); }; }
                var twice = new PerformanceObserver(record('twice')); twice.observe({ type: 'mark', buffered: true }); twice.observe({ type: 'mark', buffered: true });
                var types = new PerformanceObserver(record('types')); types.observe({ entryTypes: ['mark'], buffered: true });
                var both = new PerformanceObserver(record('both')); both.observe({ type: 'mark' }); both.observe({ type: 'measure' });
                var gone = new PerformanceObserver(record('gone')); gone.observe({ type: 'mark' }); gone.disconnect();
                var nav = new PerformanceObserver(record('nav')); nav.observe({ type: 'navigation', buffered: true });
                var bogus = new PerformanceObserver(record('bogus')); bogus.observe({ entryTypes: ['bogus', 'mark'] });
                new PerformanceObserver(record('unknown')).observe({ type: 'bogus' });
                performance.mark('c', { startTime: 20 });
                setTimeout(function () {
                  performance.mark('t', { startTime: 30 }); performance.measure('m', { start: 25, end: 26 });
                  setTimeout(function () { done([r.twice, r.types, r.both.replace(/^mark:c /, ''), r.gone || 'none', r.nav, r.bogus, r.unknown || 'none'].join('|')); }, 50);
                }, 10);
                """));
    }

    /// <summary>What <c>observe</c> and the constructor refuse, with Chromium's messages.</summary>
    [Theory]
    [InlineData("new PerformanceObserver(function () {}).observe()", "TypeError: Failed to execute 'observe' on 'PerformanceObserver': An observe() call must include either entryTypes or type arguments.")]
    [InlineData("new PerformanceObserver(function () {}).observe({ entryTypes: ['mark'], type: 'mark' })", "TypeError: Failed to execute 'observe' on 'PerformanceObserver': An observe() call must not include both entryTypes and type arguments.")]
    [InlineData("new PerformanceObserver()", "TypeError: Failed to construct 'PerformanceObserver': 1 argument required, but only 0 present.")]
    [InlineData("new PerformanceObserver(1)", "TypeError: Failed to construct 'PerformanceObserver': parameter 1 is not of type 'Function'.")]
    [InlineData("PerformanceObserver(function () {})", "TypeError: Failed to construct 'PerformanceObserver': Please use the 'new' operator, this DOM object constructor cannot be called as a function.")]
    [InlineData("(function () { var o = new PerformanceObserver(function () {}); o.observe({ type: 'mark' }); o.observe({ entryTypes: ['measure'] }); })()", "InvalidModificationError: Failed to execute 'observe' on 'PerformanceObserver': This PerformanceObserver has performed observe({type:...}, therefore it cannot perform observe({entryTypes:...})")]
    [InlineData("(function () { var o = new PerformanceObserver(function () {}); o.observe({ entryTypes: ['measure'] }); o.observe({ type: 'mark' }); })()", "InvalidModificationError: Failed to execute 'observe' on 'PerformanceObserver': This observer has performed observe({entryTypes:...}, therefore it cannot perform observe({type:...})")]
    [InlineData("PerformanceObserver.prototype.observe.call({}, { type: 'mark' })", "TypeError: Illegal invocation")]
    [InlineData("new PerformanceObserverEntryList()", "TypeError: Failed to construct 'PerformanceObserverEntryList': Illegal constructor")]
    public void ObserveRefusesAsChromiumDoes(string expression, string expected) =>
        Assert.Equal(expected, Run($"done(fail(function () {{ return {expression}; }}));"));

    /// <summary>
    /// A callback that throws does not keep the next observer from its call, and the page's navigation
    /// entry reaches an observer waiting for one once the load event has ended, with its duration.
    /// </summary>
    [Fact]
    public void ACallbackThatThrowsAndTheNavigationAtLoad()
    {
        Assert.Equal("a b|navigation true complete", Run("""
            var order = [], nav = '';
            new PerformanceObserver(function () { order.push('a'); throw new Error('boom'); }).observe({ type: 'mark' });
            new PerformanceObserver(function () { order.push('b'); }).observe({ type: 'mark' });
            performance.mark('thrower');
            new PerformanceObserver(function (list) {
              var e = list.getEntries()[0]; nav = [e.entryType, e.duration > 0, document.readyState].join(' ');
            }).observe({ type: 'navigation' });
            window.addEventListener('load', function () { setTimeout(function () { done(order.join(' ') + '|' + nav); }, 50); });
            """));
    }

    /// <summary>
    /// Each document keeps its own timeline: a frame's marks and observers are its own, the page does
    /// not see them nor the frame the page's, and its navigation entry is its own.
    /// </summary>
    [Fact]
    public void AFrameKeepsItsOwnTimeline()
    {
        const string frameScript =
            "performance.mark('frame-mark');" +
            "new PerformanceObserver(function (list) { parent.document.getElementById('heard').textContent = list.getEntries().map(function (e) { return e.name; }).join(); }).observe({ type: 'mark' });" +
            "parent.document.getElementById('frame').textContent = [performance.getEntriesByType('mark').map(function (e) { return e.name; }).join()," +
            " performance.getEntriesByType('navigation')[0].name].join(' ');" +
            "performance.mark('frame-second');";
        Assert.Equal("page-mark https://example.test/page|frame-mark about:srcdoc|frame-second", Run(
            "performance.mark('page-mark');" +
            "window.addEventListener('load', function () { setTimeout(function () { done([" +
            " performance.getEntriesByType('mark').map(function (e) { return e.name; }).join() + ' ' + performance.getEntriesByType('navigation')[0].name," +
            " document.getElementById('frame').textContent, document.getElementById('heard').textContent].join('|')); }, 50); });",
            $"<span id=\"frame\"></span><span id=\"heard\"></span><iframe srcdoc=\"{WebUtility.HtmlEncode("<script>" + frameScript + "</script>")}\"></iframe>"));
    }
}
