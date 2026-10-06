using System.Net;
using System.Text.Json;
using Broiler.HtmlBridge;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// <c>setTimeout</c> and <c>setInterval</c> hand their callback the arguments that followed the delay,
/// and run it with the window -- or the worker's global -- that registered it as <c>this</c>: HTML's
/// timer initialization steps. Every expectation is Chromium's, measured with the same scripts.
/// </summary>
/// <remarks>
/// The arguments were dropped and <c>this</c> was <c>undefined</c>. reCAPTCHA's checkbox frame hands its
/// batches of mouse events to a timer this way (<c>setTimeout.apply(null, [handler, 0, batch])</c>), and
/// its handler threw on every move, reading the batch it never got.
/// </remarks>
public class TimerArgumentTests
{
    private const string Page = "<!DOCTYPE html><html><head></head><body><div id=\"out\"></div></body></html>";

    // `done(v)` writes v into #out; `kind(v)` names a receiver.
    private const string Helpers =
        "function done(v) { document.getElementById('out').textContent = String(v); }" +
        "function kind(v) { return v === window ? 'window' : v === undefined ? 'undefined' : typeof v; }";

    private static string Run(string script, string page = Page)
    {
        var rendered = new ScriptEngine().Execute([Helpers, script], page, "https://example.test/page");
        Assert.NotNull(rendered);
        return PageProbe.OutOf(rendered!, decode: true);
    }

    [Theory]
    // Strict code sees the window as `this`, and every argument after the delay -- an explicit undefined
    // too -- by position and in `arguments`.
    [InlineData("setTimeout(function (a, b) { 'use strict'; done([kind(this), a, b, arguments.length]); }, 0, 1, 'x');", "window,1,x,2")]
    [InlineData("setTimeout(function () { done([kind(this), arguments.length]); }, 0);", "window,0")]
    [InlineData("setTimeout(function () { done(arguments.length); }, 0, undefined);", "1")]
    // The spelling reCAPTCHA uses.
    [InlineData("setTimeout.apply(null, [function (p, q) { 'use strict'; done([kind(this), p, q]); }, 0, 'p', 'q']);", "window,p,q")]
    // The values are the ones passed, not copies of them.
    [InlineData("var o = { v: 1 }; setTimeout(function (x) { done([x === o, x.v]); }, 0, o); o.v = 2;", "true,2")]
    // Every tick of an interval gets the same arguments.
    [InlineData("var ticks = [], id = setInterval(function (a, b) { 'use strict';" +
                " ticks.push([kind(this), a, b, arguments.length].join(' '));" +
                " if (ticks.length === 2) { clearInterval(id); done(ticks.join('|')); } }, 0, 'i', 2);",
        "window i 2 2|window i 2 2")]
    // One argument is required, the handler, and what is not a function still gets an id.
    [InlineData("done([setTimeout.length, setInterval.length, typeof setTimeout({}, 0, 1, 2)]);", "1,1,number")]
    public void ATimerRunsWithItsArgumentsAndTheWindow(string script, string expected) =>
        Assert.Equal(expected, Run(script));

    /// <summary>
    /// A frame's timer runs with the frame's window as <c>this</c>, in strict and in sloppy code alike:
    /// sloppy code's missing receiver would otherwise be the global object, which is the page's window.
    /// </summary>
    [Fact]
    public void AFramesTimerRunsWithTheFramesWindow()
    {
        const string frameScript =
            "function who(v) { return v === window ? 'frame' : v === parent ? 'parent' : String(v); }" +
            "setTimeout(function (a) { 'use strict'; var seen = [who(this), a];" +
            " setTimeout(function (b) { seen.push(who(this), b);" +
            "  parent.document.getElementById('out').textContent = seen.join(','); }, 0, 'fb'); }, 0, 'fa');";
        var page = "<!DOCTYPE html><html><head></head><body><div id=\"out\"></div>" +
                   $"<iframe srcdoc=\"{WebUtility.HtmlEncode("<script>" + frameScript + "</script>")}\"></iframe></body></html>";

        Assert.Equal("frame,fa,frame,fb", Run("", page));
    }

    /// <summary>
    /// A worker's timers run with its global as <c>this</c> and with their arguments, and take one
    /// required argument, as a window's do.
    /// </summary>
    [Fact]
    public void AWorkersTimerRunsWithItsGlobalAndItsArguments()
    {
        const string worker =
            "setTimeout(function (a, b) { 'use strict'; var seen = [this === self, a, b, arguments.length], n = 0;" +
            " var id = setInterval(function (x) { 'use strict'; if (++n === 2) { clearInterval(id);" +
            "  postMessage(seen.concat([this === self, x, setTimeout.length, setInterval.length]).join(',')); } }, 0, 'iv');" +
            "}, 0, 1, 'x');";

        Assert.Equal("true,1,x,2,true,iv,1,1", WorkerProbe.Answer(
            $"new Worker('data:text/javascript,' + encodeURIComponent({JsonSerializer.Serialize(worker)}))"));
    }
}

/// <summary>
/// Runs a page that starts a worker and waits, in real time, for the first message it posts: a worker's
/// timers run on its own thread and clock, so what they post arrives after the page has settled.
/// </summary>
internal static class WorkerProbe
{
    private static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>What the worker <paramref name="workerExpression"/> makes posts first, on a page at <paramref name="pageUrl"/>.</summary>
    internal static string Answer(string workerExpression, string pageUrl = "https://example.test/page")
    {
        const string waiting = "waiting";
        using var session = new ScriptEngine().ExecuteInteractive(
            [$"var w = {workerExpression}; w.onmessage = function (e) {{ document.getElementById('out').textContent = String(e.data); }};" +
             "w.onerror = function (e) { document.getElementById('out').textContent = 'error ' + e.message; };"],
            [],
            $"<!DOCTYPE html><html><head></head><body><div id=\"out\">{waiting}</div></body></html>",
            pageUrl);
        Assert.NotNull(session);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var html = session!.CurrentHtml();
            var answer = PageProbe.OutOf(html, decode: true);
            if (answer != waiting)
                return answer;

            if (clock.Elapsed > ReplyTimeout)
                Assert.Fail($"The worker did not answer within {ReplyTimeout.TotalSeconds} s: {html}");

            if (session.HasPendingWork)
                session.Step();
            else
                Thread.Sleep(10);
        }
    }
}
