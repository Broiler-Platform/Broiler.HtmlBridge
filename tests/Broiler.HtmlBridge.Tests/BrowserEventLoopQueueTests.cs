using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.JSeal;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Direct unit tests of the task queues in <see cref="BrowserEventLoop"/>: registration,
/// cancellation, deadline ordering and drain. <c>BrowserEventLoopTests</c> asserts the same loop
/// from page script, through <see cref="ScriptEngine"/> and <see cref="InteractiveSession"/>; these
/// drive it without a document, with callbacks minted in a realm the loop invokes them through.
/// </summary>
public class BrowserEventLoopQueueTests
{
    // ------------------------------------------------------------------
    //  Timeouts
    // ------------------------------------------------------------------

    [Fact]
    public void ATimeoutRunsOnceAndIsThenRemoved()
    {
        using var realm = TestRealm.Create();
        var loop = new BrowserEventLoop(() => realm);
        var runs = 0;

        loop.SetTimeout(realm.Callback(() => runs++));

        Assert.True(loop.HasPendingWork);
        Assert.True(loop.DrainStep(null));
        Assert.Equal(1, runs);
        Assert.False(loop.HasPendingWork);
        Assert.False(loop.DrainStep(null)); // one-shot: nothing left
        Assert.Equal(1, runs);
    }

    [Fact]
    public void TimeoutsFireInDeadlineOrderNotRegistrationOrder()
    {
        // setTimeout(late, 100); setTimeout(early, 0); setTimeout(mid, 50) => early, mid, late.
        using var realm = TestRealm.Create();
        var loop = new BrowserEventLoop(() => realm);
        var order = new List<string>();

        loop.SetTimeout(realm.Callback(() => order.Add("late")), 100);
        loop.SetTimeout(realm.Callback(() => order.Add("early")), 0);
        loop.SetTimeout(realm.Callback(() => order.Add("mid")), 50);
        loop.DrainAll(null);

        Assert.Equal(["early", "mid", "late"], order);
    }

    [Fact]
    public void TimeoutsWithEqualDelaysFireInRegistrationOrder()
    {
        // Equal deadlines keep registration order: the tiebreak HTML requires.
        using var realm = TestRealm.Create();
        var loop = new BrowserEventLoop(() => realm);
        var order = new List<string>();

        loop.SetTimeout(realm.Callback(() => order.Add("first")), 0);
        loop.SetTimeout(realm.Callback(() => order.Add("second")), 0);
        loop.SetTimeout(realm.Callback(() => order.Add("third")), 0);
        loop.DrainAll(null);

        Assert.Equal(["first", "second", "third"], order);
    }

    [Fact]
    public void ATimeoutWithoutAFunctionStillGetsAnId()
    {
        using var realm = TestRealm.Create();
        var loop = new BrowserEventLoop(() => realm);

        var first = loop.SetTimeout(JsValue.Undefined);
        var second = loop.SetTimeout(JsValue.String("not a function"));

        Assert.NotEqual(first, second);
        Assert.False(loop.HasPendingWork); // nothing stored for a callback that cannot be called
    }

    [Fact]
    public void ClearTimeoutDropsThePendingWork()
    {
        using var realm = TestRealm.Create();
        var loop = new BrowserEventLoop(() => realm);
        var runs = 0;

        var id = loop.SetTimeout(realm.Callback(() => runs++));
        loop.ClearTimeout(id);

        Assert.False(loop.HasPendingWork);
        Assert.False(loop.DrainStep(null));
        Assert.Equal(0, runs);
    }

    // ------------------------------------------------------------------
    //  Intervals
    // ------------------------------------------------------------------

    [Fact]
    public void AnIntervalTicksOncePerStepAndStaysRegistered()
    {
        using var realm = TestRealm.Create();
        var loop = new BrowserEventLoop(() => realm);
        var runs = 0;

        loop.SetInterval(realm.Callback(() => runs++));

        Assert.True(loop.DrainStep(null));
        Assert.True(loop.DrainStep(null));
        Assert.Equal(2, runs); // still registered after the first tick
    }

    [Fact]
    public void ClearIntervalStopsTheTicks()
    {
        using var realm = TestRealm.Create();
        var loop = new BrowserEventLoop(() => realm);
        var runs = 0;

        var id = loop.SetInterval(realm.Callback(() => runs++));
        loop.DrainStep(null);
        loop.ClearInterval(id);
        loop.DrainStep(null);

        Assert.Equal(1, runs);
    }

    [Fact]
    public void AFastIntervalTicksByItsPeriodBeforeASlowerTimeout()
    {
        // setInterval(10) ticks at 10, 20 and 30 before setTimeout(35) fires: the interval is
        // interleaved with the timeout by deadline, not merely once per drain step.
        using var realm = TestRealm.Create();
        var loop = new BrowserEventLoop(() => realm);
        var order = new List<string>();

        var id = loop.SetInterval(realm.Callback(() => order.Add("tick")), 10);
        loop.SetTimeout(realm.Callback(() =>
        {
            order.Add("timeout");
            loop.ClearInterval(id);
        }), 35);
        loop.DrainAll(null);

        Assert.Equal(["tick", "tick", "tick", "timeout"], order);
    }

    // ------------------------------------------------------------------
    //  Animation frames & frame actions
    // ------------------------------------------------------------------

    [Fact]
    public void AnAnimationFrameCallbackRunsOnce()
    {
        using var realm = TestRealm.Create();
        var loop = new BrowserEventLoop(() => realm);
        var runs = 0;

        loop.RequestAnimationFrame(realm.Callback(() => runs++));

        Assert.True(loop.DrainStep(null));
        Assert.False(loop.DrainStep(null));
        Assert.Equal(1, runs);
    }

    [Fact]
    public void CancelAnimationFrameDropsTheCallback()
    {
        using var realm = TestRealm.Create();
        var loop = new BrowserEventLoop(() => realm);
        var runs = 0;

        var id = loop.RequestAnimationFrame(realm.Callback(() => runs++));
        loop.CancelAnimationFrame(id);

        Assert.False(loop.HasPendingWork);
        Assert.Equal(0, runs);
    }

    [Fact]
    public void AFrameActionRunsOnTheNextStep()
    {
        using var realm = TestRealm.Create();
        var loop = new BrowserEventLoop(() => realm);
        var ran = false;

        loop.QueueFrameAction(() => ran = true);

        Assert.True(loop.HasPendingWork);
        Assert.True(loop.DrainStep(null));
        Assert.True(ran);
    }

    // ------------------------------------------------------------------
    //  Drain semantics
    // ------------------------------------------------------------------

    [Fact]
    public void AnEmptyLoopHasNothingToDrain()
    {
        using var realm = TestRealm.Create();

        Assert.False(new BrowserEventLoop(() => realm).DrainStep(null));
    }

    [Fact]
    public void DrainAllRunsTimeoutsQueuedDuringTheDrain()
    {
        using var realm = TestRealm.Create();
        var loop = new BrowserEventLoop(() => realm);
        var runs = 0;
        var second = realm.Callback(() => runs++);
        var first = realm.Callback(() =>
        {
            runs++;
            loop.SetTimeout(second); // queued mid-drain
        });

        loop.SetTimeout(first);
        loop.DrainAll(null);

        Assert.Equal(2, runs);
        Assert.False(loop.HasPendingWork);
    }

    [Fact]
    public void ATaskCheckpointRunsAfterEveryTask()
    {
        using var realm = TestRealm.Create();
        var loop = new BrowserEventLoop(() => realm);
        loop.SetTimeout(realm.Callback(() => { }));
        loop.SetTimeout(realm.Callback(() => { }));
        var checkpoints = 0;

        loop.DrainStep(() => checkpoints++);

        Assert.Equal(2, checkpoints);
    }

    [Fact]
    public void AThrowingCallbackDoesNotAbortTheBatch()
    {
        using var realm = TestRealm.Create();
        var loop = new BrowserEventLoop(() => realm);
        var goodRan = false;
        loop.SetTimeout(realm.NewMethod("bad", (in JsCall _) => throw new InvalidOperationException("boom")));
        loop.SetTimeout(realm.Callback(() => goodRan = true));

        var ran = loop.DrainStep(null); // must not throw

        Assert.True(ran);
        Assert.True(goodRan);
    }

    [Fact]
    public void ClearDropsPendingWorkWithoutRunningIt()
    {
        using var realm = TestRealm.Create();
        var loop = new BrowserEventLoop(() => realm);
        var runs = 0;
        loop.SetTimeout(realm.Callback(() => runs++));
        loop.RequestAnimationFrame(realm.Callback(() => runs++));
        loop.QueueFrameAction(() => runs++);

        loop.Clear();

        Assert.False(loop.HasPendingWork);
        Assert.False(loop.DrainStep(null));
        Assert.Equal(0, runs);
    }
}
