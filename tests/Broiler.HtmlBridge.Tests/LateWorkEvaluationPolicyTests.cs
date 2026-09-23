using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Scripting;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// What work a script leaves behind on <c>ScriptEngine</c>'s document-free entry points,
/// <c>Execute(scripts)</c> and <c>ExecuteDetailed(scripts)</c>, may compile once the call has returned.
/// </summary>
/// <remarks>
/// <para>
/// <b>The refusal used to end with the call.</b> Under a policy withholding <c>'unsafe-eval'</c>, a script
/// running inside the call could not compile a string. A callback it scheduled on the engine's own timers,
/// or a promise reaction the engine posted outside its queue, ran after the call had returned and compiled
/// whatever it asked for; only <c>eval</c> was still answered, by the stub.
/// </para>
/// <para>
/// <b>Late work is held and run by the test, after the call.</b> While the entry point runs, the calling
/// thread's <c>SynchronizationContext</c> is one that only holds what is posted to it, so the context the
/// entry point builds captures it: <c>setTimeout</c> and <c>setInterval</c> post their callbacks there,
/// and so does a reaction scheduled while no script is executing. (<c>Promise.allKeyed</c> used to be a
/// schedule here as well, because Broiler.JS 0.1.0-preview.1 posted its element settlements there; from
/// preview.3 they are ordinary promise jobs the call drains before returning, so it schedules no late
/// work.) <c>setImmediate</c> posts to the synchronization context current on the thread when
/// it is called, which during the call is that same one. Nothing held runs until the test pumps it, after
/// the entry point has returned, on the thread that made the call, on a pool thread, or on a thread that
/// last ran a context under the opposite policy.
/// </para>
/// <para>
/// <b>The outcome comes back as a count, never through a route under test.</b> The late callback makes one
/// attempt inside its own try/catch and then calls <c>queueMicrotask</c> three times if the attempt
/// completed (for a compiling route, that means it compiled), once if the attempt threw a
/// <c>SyntaxError</c>, and twice if it threw anything else. <c>queueMicrotask</c>
/// enqueues onto <c>ScriptEngine.MicroTasks</c>, which the call drained before returning, so the count the
/// test reads afterwards is exactly what the late work added. A count of zero means the late work never ran,
/// which fails the test rather than reading as an outcome. Work drained by the engine's next call reports
/// the same way through promise reactions posted to the held context, and the test counts those posts.
/// </para>
/// </remarks>
public class LateWorkEvaluationPolicyTests
{
    private const string Forbidding = "script-src 'self'";
    private const string Permitting = "script-src 'self' 'unsafe-eval'";
    private static readonly TimeSpan PostTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A synchronization context that only holds what is posted to it, until the test runs it.</summary>
    private sealed class HeldPosts : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _posts = new();

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_posts)
                _posts.Enqueue((d, state));
        }

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        public int Pending
        {
            get
            {
                lock (_posts)
                    return _posts.Count;
            }
        }

        /// <summary>Waits, on the calling thread, until something has been posted.</summary>
        public void WaitForAPost()
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (Pending == 0 && clock.Elapsed < PostTimeout)
                Thread.Sleep(5);
            Assert.True(Pending > 0, "nothing was posted, so there was no late work to run");
        }

        /// <summary>Runs every held post, and any it posts in turn, on the current thread.</summary>
        public void RunAll()
        {
            while (true)
            {
                (SendOrPostCallback Callback, object? State) item;
                lock (_posts)
                {
                    if (_posts.Count == 0)
                        return;
                    item = _posts.Dequeue();
                }

                var previous = Current;
                SetSynchronizationContext(this);
                try
                {
                    item.Callback(item.State);
                }
                finally
                {
                    SetSynchronizationContext(previous);
                }
            }
        }
    }

    /// <summary>Where the held posts are run.</summary>
    public enum Pump
    {
        /// <summary>On the thread that made the call.</summary>
        CallingThread,

        /// <summary>On a pool thread.</summary>
        PoolThread,
    }

    private static ContentSecurityPolicy? Policy(string? text) => CspFixture.Csp(text);

    /// <summary>
    /// The body of a late callback: attempt <paramref name="attempt"/>, then report the outcome as a number of
    /// <c>queueMicrotask</c> calls.
    /// </summary>
    private static string Body(string attempt) =>
        "var noop = function () {};" +
        $"try {{ {attempt}; queueMicrotask(noop); queueMicrotask(noop); queueMicrotask(noop); }}" +
        "catch (e) { if (e instanceof SyntaxError) { queueMicrotask(noop); } else { queueMicrotask(noop); queueMicrotask(noop); } }";

    private static string Callback(string attempt) => $"function () {{ {Body(attempt)} }}";

    private static string Reading(int count) => count switch
    {
        3 => "compiled",
        1 => "refused:SyntaxError",
        2 => "refused:other",
        0 => "late work never ran",
        var n => $"unexpected count {n}",
    };

    /// <summary>Runs <paramref name="script"/> with <paramref name="posts"/> held, and checks the call itself.</summary>
    private static void Call(ScriptEngine engine, HeldPosts posts, string script, bool detailed)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(posts);
        try
        {
            if (detailed)
                Assert.True(engine.ExecuteDetailed([script]).Success, "the script itself failed");
            else
                Assert.True(engine.Execute([script]), "the script itself failed");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        Assert.Equal(0, engine.MicroTasks.Count);
    }

    /// <summary>
    /// Runs <paramref name="script"/> through a document-free entry point under <paramref name="policy"/>, then
    /// runs the work it left behind on <paramref name="pump"/> and answers what that work met.
    /// </summary>
    private static async Task<string> LateOutcome(string? policy, string script, Pump pump = Pump.CallingThread, bool detailed = false)
    {
        var posts = new HeldPosts();
        var engine = new ScriptEngine { Csp = Policy(policy) };

        Call(engine, posts, script, detailed);
        posts.WaitForAPost();

        if (pump == Pump.PoolThread)
            await Task.Run(posts.RunAll);
        else
            posts.RunAll();

        return Reading(engine.MicroTasks.Count);
    }

    /// <summary>
    /// As <see cref="LateOutcome"/>, but the held posts run on a dedicated thread that first ran a document-free
    /// call of its own under <paramref name="ambientPolicy"/>, the opposite policy. That call leaves its context
    /// current on the thread after it returns, so that context is current as each held post begins, until the
    /// work enters its own context; nothing in this file asserts that.
    /// </summary>
    private static string LateOutcomeOnAThreadThatRanAnotherPolicy(string? policy, string script, string? ambientPolicy)
    {
        var posts = new HeldPosts();
        var engine = new ScriptEngine { Csp = Policy(policy) };
        Call(engine, posts, script, detailed: false);
        posts.WaitForAPost();

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Assert.True(new ScriptEngine { Csp = Policy(ambientPolicy) }.Execute(["var ambient = 1;"]));
                posts.RunAll();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.Start();
        Assert.True(thread.Join(PostTimeout), "the pumping thread did not finish");
        Assert.Null(failure);

        return Reading(engine.MicroTasks.Count);
    }

    /// <summary>Ways a script can schedule work that runs after the call; <c>CALLBACK</c> is the callback.</summary>
    public static TheoryData<string> Schedules => new()
    {
        "setTimeout(CALLBACK, 0);",
        "setInterval(CALLBACK, 0);",
        "setImmediate(CALLBACK);",
        "queueMicrotask(function () { Promise.resolve().then(CALLBACK); });",
        "queueMicrotask(function () { (async function () { await null; (CALLBACK)(); })(); });",
    };

    /// <summary>The compiling routes a late callback might attempt, and what each is refused with.</summary>
    public static TheoryData<string, string> Attempts => new()
    {
        { "new Function('return 7')()", "refused:SyntaxError" },
        { "Function('a', 'b', 'return a + b')(3, 4)", "refused:SyntaxError" },
        { "(function () {}).constructor('return 7')()", "refused:SyntaxError" },
        { "Reflect.construct(Function, ['return 7'])()", "refused:SyntaxError" },
        { "typeof new Function()", "refused:SyntaxError" },
        { "typeof new (Object.getPrototypeOf(async function () {}).constructor)()", "refused:SyntaxError" },
        { "typeof new (Object.getPrototypeOf(function* () {}).constructor)()", "refused:SyntaxError" },
        { "typeof new (Object.getPrototypeOf(async function* () {}).constructor)()", "refused:SyntaxError" },
        { "new ShadowRealm().evaluate('6 * 7')", "refused:SyntaxError" },
        // eval is the stub's, which stays on the context, and its error is not a SyntaxError.
        { "eval('6 * 7')", "refused:other" },
        { "(0, eval)('6 * 7')", "refused:other" },
    };

    private static IEnumerable<string> ScheduleList => Schedules.Select(row => (string)row[0]);

    private static IEnumerable<(string Attempt, string Refusal)> AttemptList =>
        Attempts.Select(row => ((string)row[0], (string)row[1]));

    /// <summary>Every schedule crossed with every attempt, pumped on either thread.</summary>
    public static TheoryData<string, string, string, Pump> RoutesAndAttempts()
    {
        var data = new TheoryData<string, string, string, Pump>();
        foreach (var schedule in ScheduleList)
        foreach (var (attempt, refusal) in AttemptList)
        foreach (var pump in new[] { Pump.CallingThread, Pump.PoolThread })
            data.Add(schedule, attempt, refusal, pump);
        return data;
    }

    /// <summary>Shapes of callable a script can hand <c>setTimeout</c>.</summary>
    public static TheoryData<string> CallbackKinds => new()
    {
        "function () { BODY }",
        "() => { BODY }",
        "(function () { BODY }).bind(null)",
        "async function () { BODY }",
        "({ m() { BODY } }).m",
        "(class { static m() { BODY } }).m",
    };

    /// <summary><c>ExecuteDetailed(scripts)</c> rows: a subset of schedules and attempts, pumped on either thread.</summary>
    public static TheoryData<string, string, string, Pump> DetailedRows()
    {
        var data = new TheoryData<string, string, string, Pump>();
        var schedules = new[]
        {
            "setTimeout(CALLBACK, 0);",
            "setImmediate(CALLBACK);",
            "queueMicrotask(function () { Promise.resolve().then(CALLBACK); });",
        };
        var attempts = new[]
        {
            ("new Function('return 7')()", "refused:SyntaxError"),
            ("new ShadowRealm().evaluate('6 * 7')", "refused:SyntaxError"),
            ("eval('6 * 7')", "refused:other"),
        };
        foreach (var schedule in schedules)
        foreach (var (attempt, refusal) in attempts)
        foreach (var pump in new[] { Pump.CallingThread, Pump.PoolThread })
            data.Add(schedule, attempt, refusal, pump);
        return data;
    }

    /// <summary>Every schedule crossed with every non-eval attempt, for the controls.</summary>
    public static TheoryData<string, string> SchedulesAndCompilingAttempts()
    {
        var data = new TheoryData<string, string>();
        foreach (var schedule in ScheduleList)
        foreach (var (attempt, refusal) in AttemptList)
        {
            if (refusal == "refused:SyntaxError")
                data.Add(schedule, attempt);
        }

        return data;
    }

    // -- refused: work left behind meets the call's policy -----------------------------------------------

    /// <summary>
    /// Under a forbidding policy, work that runs after <c>Execute(scripts)</c> has returned is refused each
    /// compiling route in <see cref="Attempts"/>, on each schedule in <see cref="Schedules"/>, pumped on the
    /// calling thread or on a pool thread; <c>eval</c> meets the stub.
    /// </summary>
    [Theory]
    [MemberData(nameof(RoutesAndAttempts))]
    public async Task LateWorkIsRefusedEachListedCompilingRoute(string schedule, string attempt, string refusal, Pump pump)
    {
        Assert.Equal(refusal, await LateOutcome(Forbidding, schedule.Replace("CALLBACK", Callback(attempt)), pump));
    }

    /// <summary>Under a forbidding policy, a late <c>new Function</c> call is refused from each shape of callable in <see cref="CallbackKinds"/> handed to <c>setTimeout</c>, pumped on the calling thread and on a pool thread.</summary>
    [Theory]
    [MemberData(nameof(CallbackKinds))]
    public async Task ALateTimeoutCallbackOfEachListedKindIsRefused(string kind)
    {
        var script = $"setTimeout({kind.Replace("BODY", Body("new Function('return 7')()"))}, 0);";
        Assert.Equal("refused:SyntaxError", await LateOutcome(Forbidding, script));
        Assert.Equal("refused:SyntaxError", await LateOutcome(Forbidding, script, Pump.PoolThread));
    }

    /// <summary>
    /// A reaction whose handler is the <c>Function</c> constructor itself compiles through the reaction job
    /// rather than through a call the script makes, and meets the same refusal; under a permitting policy it
    /// compiles.
    /// </summary>
    [Theory]
    [InlineData(Forbidding, Pump.CallingThread, "refused:SyntaxError")]
    [InlineData(Forbidding, Pump.PoolThread, "refused:SyntaxError")]
    [InlineData(Permitting, Pump.CallingThread, "compiled")]
    public async Task AReactionWhoseHandlerIsTheFunctionConstructorMeetsThePolicy(string policy, Pump pump, string expected)
    {
        const string script =
            "var noop = function () {};" +
            "function three() { queueMicrotask(noop); queueMicrotask(noop); queueMicrotask(noop); }" +
            "function report(e) { if (e instanceof SyntaxError) { queueMicrotask(noop); } else { queueMicrotask(noop); queueMicrotask(noop); } }" +
            "queueMicrotask(function () { Promise.resolve('return 7').then(Function).then(three, report); });";
        Assert.Equal(expected, await LateOutcome(policy, script, pump));
    }

    /// <summary><c>ExecuteDetailed(scripts)</c> leaves the same refusal behind.</summary>
    [Theory]
    [MemberData(nameof(DetailedRows))]
    public async Task ExecuteDetailedLeavesTheSameRefusalBehind(string schedule, string attempt, string refusal, Pump pump)
    {
        Assert.Equal(refusal, await LateOutcome(Forbidding, schedule.Replace("CALLBACK", Callback(attempt)), pump, detailed: true));
    }

    /// <summary>
    /// The refusal belongs to the context the work was left on, not to the thread that runs it: late work from a
    /// forbidding call is refused on a thread that last ran a permitting context, and late work from a
    /// permitting call compiles on a thread that last ran a forbidding one.
    /// </summary>
    [Theory]
    [InlineData("setTimeout(CALLBACK, 0);", "new Function('return 7')()")]
    [InlineData("setTimeout(CALLBACK, 0);", "new ShadowRealm().evaluate('6 * 7')")]
    [InlineData("setImmediate(CALLBACK);", "new Function('return 7')()")]
    [InlineData("setImmediate(CALLBACK);", "new ShadowRealm().evaluate('6 * 7')")]
    [InlineData("queueMicrotask(function () { Promise.resolve().then(CALLBACK); });", "new Function('return 7')()")]
    [InlineData("queueMicrotask(function () { Promise.resolve().then(CALLBACK); });", "new ShadowRealm().evaluate('6 * 7')")]
    public void TheRefusalIsTheContextsNotTheThreads(string schedule, string attempt)
    {
        var script = schedule.Replace("CALLBACK", Callback(attempt));
        Assert.Equal("refused:SyntaxError", LateOutcomeOnAThreadThatRanAnotherPolicy(Forbidding, script, ambientPolicy: null));
        Assert.Equal("compiled", LateOutcomeOnAThreadThatRanAnotherPolicy(Permitting, script, ambientPolicy: Forbidding));
    }

    /// <summary>
    /// Work left over on <c>ScriptEngine.MicroTasks</c> is drained by the engine's next call, and meets the policy
    /// of the call that left it, not the next call's: left by a forbidding call and drained by a call with no
    /// policy, it is refused; left by a permitting call and drained by a forbidding one, it compiles.
    /// </summary>
    [Theory]
    [InlineData(Forbidding, null, 1)]
    [InlineData(Permitting, Forbidding, 3)]
    public void LeftoverWorkMeetsThePolicyOfTheCallThatLeftIt(string firstPolicy, string? secondPolicy, int expectedPosts)
    {
        // The late callback queues a microtask; that microtask, drained by the next call, attempts the route and
        // reports through promise reactions, which are posted to the first call's held context.
        const string leftover =
            "var noop = function () {};" +
            "function report(n) { for (var i = 0; i < n; i++) { Promise.resolve().then(noop); } }" +
            "setTimeout(function () { queueMicrotask(function () {" +
            "  try { new Function('return 7')(); report(3); }" +
            "  catch (e) { if (e instanceof SyntaxError) { report(1); } else { report(2); } }" +
            "}); }, 0);";

        var posts = new HeldPosts();
        var engine = new ScriptEngine { Csp = Policy(firstPolicy) };
        Call(engine, posts, leftover, detailed: false);
        posts.WaitForAPost();
        posts.RunAll();

        Assert.Equal(1, engine.MicroTasks.Count);
        Assert.Equal(0, posts.Pending);

        engine.Csp = Policy(secondPolicy);
        Assert.True(engine.Execute(["var next = 1;"]));

        Assert.Equal(0, engine.MicroTasks.Count);
        Assert.Equal(expectedPosts, posts.Pending);
    }

    // -- controls: late work runs, and compiles when the policy allows it ----------------------------------

    /// <summary>
    /// Under a permitting policy, or with no policy, each non-<c>eval</c> attempt in <see cref="Attempts"/> compiles
    /// in late work on each schedule in <see cref="Schedules"/>, pumped on the calling thread: no such attempt
    /// throws a <c>SyntaxError</c> for a reason of its own, so the calling-thread <c>SyntaxError</c> refusals of
    /// the same rows in <see cref="LateWorkIsRefusedEachListedCompilingRoute"/> are the policy's.
    /// </summary>
    [Theory]
    [MemberData(nameof(SchedulesAndCompilingAttempts))]
    public async Task LateWorkCompilesEachListedRouteWhenThePolicyAllowsIt(string schedule, string attempt)
    {
        var script = schedule.Replace("CALLBACK", Callback(attempt));
        Assert.Equal("compiled", await LateOutcome(Permitting, script));
        Assert.Equal("compiled", await LateOutcome(null, script));
    }

    /// <summary>The ShadowRealm route compiles on a pool thread too when the policy allows it.</summary>
    [Theory]
    [MemberData(nameof(Schedules))]
    public async Task ShadowRealmEvaluateCompilesOnAPoolThreadWhenThePolicyAllowsIt(string schedule)
    {
        var script = schedule.Replace("CALLBACK", Callback("new ShadowRealm().evaluate('6 * 7')"));
        Assert.Equal("compiled", await LateOutcome(Permitting, script, Pump.PoolThread));
        Assert.Equal("compiled", await LateOutcome(null, script, Pump.PoolThread));
    }

    /// <summary><c>eval</c> in a <c>setTimeout</c> callback pumped on the calling thread compiles under a permitting policy or none, so its refusal above on that schedule and thread comes with the policy.</summary>
    [Theory]
    [InlineData("eval('6 * 7')")]
    [InlineData("(0, eval)('6 * 7')")]
    public async Task LateEvalCompilesWhenThePolicyAllowsIt(string attempt)
    {
        var script = $"setTimeout({Callback(attempt)}, 0);";
        Assert.Equal("compiled", await LateOutcome(Permitting, script));
        Assert.Equal("compiled", await LateOutcome(null, script));
    }

    /// <summary><c>ExecuteDetailed(scripts)</c> leaves compiling late work compiling when the policy allows it.</summary>
    [Theory]
    [InlineData("setTimeout(CALLBACK, 0);")]
    [InlineData("queueMicrotask(function () { Promise.resolve().then(CALLBACK); });")]
    public async Task ExecuteDetailedLateWorkCompilesWhenThePolicyAllowsIt(string schedule)
    {
        var script = schedule.Replace("CALLBACK", Callback("new Function('return 7')()"));
        Assert.Equal("compiled", await LateOutcome(Permitting, script, detailed: true));
        Assert.Equal("compiled", await LateOutcome(null, script, detailed: true));
    }

    /// <summary>Under a forbidding policy, late work that compiles nothing still runs to completion, which reads as <c>compiled</c> because its attempt completes.</summary>
    [Theory]
    [MemberData(nameof(Schedules))]
    public async Task LateWorkThatCompilesNothingStillRuns(string schedule)
    {
        Assert.Equal("compiled", await LateOutcome(Forbidding, schedule.Replace("CALLBACK", Callback("6 * 7"))));
    }
}
