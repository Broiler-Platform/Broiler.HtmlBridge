using System;
using System.Collections.Generic;
using System.Threading;
using Broiler.HtmlBridge;
using Broiler.HtmlBridge.Dom.Runtime;
using Xunit;
using TextRegex = System.Text.RegularExpressions.Regex;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// Verifies event loop semantics and load settling in <see cref="BrowserEventLoop"/>,
/// <see cref="ScriptEngine"/> and <see cref="InteractiveSession"/>.
/// </summary>
public class BrowserEventLoopTests
{
    private const string PageUrl = "https://example.test/event-loop";
    private const string BaseHtml = "<!DOCTYPE html><html><head></head><body><div id=\"out\"></div></body></html>";

    private static string ExtractOut(string html)
    {
        var match = TextRegex.Match(html, @"<div id=""out"">(.*?)</div>");
        Assert.True(match.Success, $"no #out div in output: {html}");
        return match.Groups[1].Value;
    }

    // =========================================================================
    //  1. Animation-Frame Cancellation
    // =========================================================================

    [Fact]
    public void Timer_CanCancel_PendingAnimationFrameCallback()
    {
        // A zero-delay timer cancels a rAF callback before it is invoked.
        // The rAF callback must not execute.
        using var session = new ScriptEngine().ExecuteInteractive(
            [
                """
                window.id = requestAnimationFrame(function() {
                    document.getElementById('out').textContent = 'ran-raf';
                });
                setTimeout(function() {
                    cancelAnimationFrame(window.id);
                    document.getElementById('out').textContent = 'timer-cancelled';
                }, 0);
                """
            ],
            [],
            BaseHtml,
            PageUrl);

        Assert.NotNull(session);
        var html = session!.Step();
        Assert.NotNull(html);
        Assert.Equal("timer-cancelled", ExtractOut(html!));
    }

    [Fact]
    public void AnimationFrameCallback_CanCancel_LaterAnimationFrameCallbackInSameBatch()
    {
        // rAF callback 1 cancels rAF callback 2 in the same batch.
        // Callback 2 must not run.
        using var session = new ScriptEngine().ExecuteInteractive(
            [
                """
                var id2 = 0;
                requestAnimationFrame(function() {
                    cancelAnimationFrame(id2);
                    document.getElementById('out').textContent += 'A';
                });
                id2 = requestAnimationFrame(function() {
                    document.getElementById('out').textContent += 'B';
                });
                """
            ],
            [],
            BaseHtml,
            PageUrl);

        Assert.NotNull(session);
        var html = session!.Step();
        Assert.NotNull(html);
        Assert.Equal("A", ExtractOut(html!));
    }

    [Fact]
    public void AnimationFrameCallback_RegisteredDuringBatch_IsDeferredToNextStep()
    {
        // A rAF callback registered during the batch must not run in the same step.
        using var session = new ScriptEngine().ExecuteInteractive(
            [
                """
                requestAnimationFrame(function() {
                    document.getElementById('out').textContent += '1';
                    requestAnimationFrame(function() {
                        document.getElementById('out').textContent += '2';
                    });
                });
                """
            ],
            [],
            BaseHtml,
            PageUrl);

        Assert.NotNull(session);
        var step1 = session!.Step();
        Assert.NotNull(step1);
        Assert.Equal("1", ExtractOut(step1!));

        var step2 = session.Step();
        Assert.NotNull(step2);
        Assert.Equal("12", ExtractOut(step2!));
    }

    // =========================================================================
    //  2. Timer Cancellation Bookkeeping
    // =========================================================================

    [Fact]
    public void CancellingNonExistentTimer_DoesNotRetainClearedIds()
    {
        var loop = new BrowserEventLoop(() => null);
        for (var i = 1; i <= 10000; i++)
        {
            loop.ClearTimeout(i);
        }

        Assert.Equal(0, loop.ClearedTimerCount);
    }

    [Fact]
    public void Timer_CanCancel_AnotherTimerInSameBatch()
    {
        // Timer 1 cancels Timer 2 scheduled at the same deadline.
        // Timer 2 must not run.
        using var session = new ScriptEngine().ExecuteInteractive(
            [
                """
                var t2 = 0;
                setTimeout(function() {
                    clearTimeout(t2);
                    document.getElementById('out').textContent += 'T1';
                }, 0);
                t2 = setTimeout(function() {
                    document.getElementById('out').textContent += 'T2';
                }, 0);
                """
            ],
            [],
            BaseHtml,
            PageUrl);

        Assert.NotNull(session);
        var html = session!.Step();
        Assert.NotNull(html);
        Assert.Equal("T1", ExtractOut(html!));
    }

    [Fact]
    public void Interval_CanCancelItself_PreventsRescheduling()
    {
        // Repeating interval cancels itself in its first callback.
        // It must fire once and not reschedule.
        using var session = new ScriptEngine().ExecuteInteractive(
            [
                """
                var ticks = 0;
                var intervalId = setInterval(function() {
                    ticks++;
                    clearInterval(intervalId);
                    document.getElementById('out').textContent = String(ticks);
                }, 0);
                """
            ],
            [],
            BaseHtml,
            PageUrl);

        Assert.NotNull(session);
        var step1 = session!.Step();
        Assert.NotNull(step1);
        Assert.Equal("1", ExtractOut(step1!));

        // After step 1, interval should not be rescheduled
        var step2 = session.Step();
        Assert.Null(step2);
        Assert.False(session.HasPendingWork);
    }

    // =========================================================================
    //  3. Load Settling Loop
    // =========================================================================

    [Fact]
    public void LoadSettling_FiniteTimerSequence_SettlesCleanly()
    {
        var engine = new ScriptEngine();
        var html = engine.Execute(
            [
                """
                var count = 0;
                function chain() {
                    count++;
                    if (count < 3) {
                        setTimeout(chain, 0);
                    } else {
                        document.getElementById('out').textContent = 'settled-' + count;
                    }
                }
                setTimeout(chain, 0);
                """
            ],
            BaseHtml,
            PageUrl);

        Assert.NotNull(html);
        Assert.Equal("settled-3", ExtractOut(html!));
        Assert.False(engine.AsyncDrainLimitExhausted);
    }

    [Fact]
    public void LoadSettling_InfiniteSelfReschedulingTimer_ExhaustsBudgetAndTerminates()
    {
        var engine = new ScriptEngine();
        var html = engine.Execute(
            [
                """
                var count = 0;
                function loop() {
                    count++;
                    document.getElementById('out').textContent = 'loop-' + count;
                    setTimeout(loop, 0);
                }
                setTimeout(loop, 0);
                """
            ],
            BaseHtml,
            PageUrl);

        Assert.NotNull(html);
        Assert.True(engine.AsyncDrainLimitExhausted);
    }

    [Fact]
    public void InteractiveSession_SettleLoadWindow_SetsExhaustedDrainBudget()
    {
        using var session = new ScriptEngine().ExecuteInteractive(
            [
                """
                function loop() {
                    setTimeout(loop, 0);
                }
                setTimeout(loop, 0);
                """
            ],
            [],
            BaseHtml,
            PageUrl);

        Assert.NotNull(session);
        var html = session!.SettleLoadWindow();
        Assert.NotNull(html);
        Assert.True(session.AsyncDrainLimitExhausted);
    }
}
