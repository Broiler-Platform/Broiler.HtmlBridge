namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A document's global has no <c>setImmediate</c>, as in every browser, so a scheduler that looks
/// for it first takes the next thing it knows.
/// </summary>
/// <remarks>
/// <c>setImmediate</c> is Broiler.JS's own global, the one Node has (Internet Explorer had one too,
/// and no browser does). The window put its event loop's <c>setTimeout</c> and <c>setInterval</c> in
/// place of the engine's and left this one, and a scheduler that finds it uses it: React's looks for
/// it before a <c>MessageChannel</c> and takes it for every render. The engine's posts its callback
/// to the calling thread's <see cref="System.Threading.SynchronizationContext"/>, which is not the
/// window's event loop, and fails with a <see cref="NullReferenceException"/>, which reaches the
/// script as a <c>ReferenceError</c>, on a thread that has none. The thread that settles a page's
/// load window has none when it runs a script the page inserted, and duckduckgo.com's React root
/// scheduled its first render from there and so never rendered.
/// </remarks>
public class NoSetImmediateTests
{
    private const string PageUrl = "https://example.test/set-immediate";
    private const string Page = "<html><body><div id=\"out\">nothing</div></body></html>";

    [Theory]
    [InlineData("typeof setImmediate", "undefined")]
    [InlineData("typeof window.setImmediate", "undefined")]
    [InlineData("typeof globalThis.setImmediate", "undefined")]
    [InlineData("'setImmediate' in window", "false")]
    // What a scheduler falls back to is still there.
    [InlineData("[typeof MessageChannel, typeof setTimeout, typeof queueMicrotask].join()", "function,function,function")]
    public void A_Document_Has_No_SetImmediate(string expression, string expected) =>
        Assert.Equal(expected, PageProbe.RunAgainst(Page, PageUrl, expression));

    /// <summary>
    /// React's scheduler, as duckduckgo.com ships it: <c>setImmediate</c> if there is one, else a
    /// <c>MessageChannel</c>, else <c>setTimeout</c>. It is called from a microtask of a script the
    /// page inserted, which runs as the load window is settled, on a thread with no synchronization
    /// context, as the browser window settles it.
    /// </summary>
    [Fact]
    public void A_Scheduler_Called_From_An_Inserted_Scripts_Microtask_Runs_Its_Work()
    {
        const string program =
            """
            var local = typeof setImmediate !== "undefined" ? setImmediate : null;
            var via, schedule;
            if (typeof local === "function") {
              via = "setImmediate";
              schedule = function (callback) { local(callback); };
            } else if (typeof MessageChannel !== "undefined") {
              via = "MessageChannel";
              var channel = new MessageChannel(), queue = [];
              channel.port1.onmessage = function () { queue.shift()(); };
              schedule = function (callback) { queue.push(callback); channel.port2.postMessage(null); };
            } else {
              via = "setTimeout";
              schedule = function (callback) { setTimeout(callback, 0); };
            }
            queueMicrotask(function () {
              var out = document.getElementById("out");
              try { schedule(function () { out.textContent = via + " ran"; }); }
              catch (e) { out.textContent = via + " threw " + e.name + ": " + e.message; }
            });
            """;
        var insert =
            "var s = document.createElement('script');" +
            $"s.src = 'data:text/javascript,{Uri.EscapeDataString(program)}';" +
            "document.body.appendChild(s);";

        string? result = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var session = new ScriptEngine().ExecuteInteractive([insert], [], Page, PageUrl);
                Assert.NotNull(session);
                result = PageProbe.OutOf(session.SettleLoadWindow());
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(2)), "the page did not finish");
        Assert.Null(failure);

        Assert.Equal("MessageChannel ran", result);
    }
}
