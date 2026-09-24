using Broiler.JavaScript.BuiltIns.Promise;
using Broiler.JavaScript.Storage;
using Broiler.JavaScript.Runtime;
using Broiler.JavaScript.Engine.Core;
using Broiler.JavaScript.BuiltIns.Function;
using Broiler.HtmlBridge.Logging;

namespace Broiler.HtmlBridge;

public sealed partial class ScriptEngine
{
    private JSValue JsScriptEngineQueueMicrotask001Core(in Arguments a, Dom.Runtime.IEngineJobs? engineJobs)
            {
                if (a.Length == 0 || a[0] is not JSFunction fn)
                    throw JSEngine.NewTypeError("Callback must be a function");
                // Queued for the browsing context whose script called it: a frame's callback runs in
                // the frame's window context, not in whichever one is current when the queue drains;
                // and behind the promise jobs its script has already queued.
                DomBridge.QueueMicrotask(MicroTasks, engineJobs, () =>
                {
                    try
                    {
                        fn.InvokeFunction(new Arguments(JSUndefined.Value));
                    }
                    catch (Exception ex)
                    {
                        RenderLogger.LogError(LogCategory.JavaScript, "ScriptEngine.queueMicrotask", $"Callback error: {ex.Message}", ex);
                    }
                });
                return JSUndefined.Value;
            }

    private JSValue JsScriptEngineEval002Core(in Arguments _)
    {
        throw new InvalidOperationException("Refused to evaluate a string as JavaScript because 'unsafe-eval' is not an allowed source in the Content Security Policy.");
    }

    /// <summary>
    /// The two things about promise jobs only this engine can do for the bridge's window job pumps,
    /// which name no engine (<see cref="Dom.Runtime.IEngineJobs"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A pump as the engine's job queue.</b> The engine runs a promise job on the current
    /// <see cref="System.Threading.SynchronizationContext"/> only when that context carries
    /// <see cref="IJSJobPump"/>, and a promise, or an <c>await</c>, created while one is current keeps
    /// it for its reactions. The wrapper forwards to the pump, which runs the job in its window's
    /// context.
    /// </para>
    /// <para>
    /// <b>A job behind the running script's.</b> The engine keeps the page's promise jobs in a queue of
    /// its own, drained when the outermost execution ends, and offers no way to add a host job to it
    /// except as a promise reaction. So that is how: a promise settled here, under a context that is
    /// not a pump, takes a reaction whose job the engine queues behind every job already there. The
    /// engine takes its own queue only while an execution is in progress; a reaction it dispatches
    /// anywhere else -- its host context or the thread pool, when nothing was executing after all --
    /// may run on another thread than the one that offered it, and then does nothing: the caller has
    /// queued the job on the bridge's queue as well (<c>OnceJob</c>). The reaction never throws, so its
    /// derived promise never rejects.
    /// </para>
    /// </remarks>
    private sealed class EngineJobs : Dom.Runtime.IEngineJobs
    {
        public static readonly EngineJobs Instance = new();

        public System.Threading.SynchronizationContext AsJobQueue(Dom.Runtime.WindowJobPump pump) => new JobQueue(pump);

        public bool TryOfferToRunningScript(Action job)
        {
            ArgumentNullException.ThrowIfNull(job);

            // A promise registers itself with the current context, so with none there is no engine
            // queue to join either.
            if (JSEngine.Current is null)
                return false;

            var thread = Environment.CurrentManagedThreadId;
            var previous = System.Threading.SynchronizationContext.Current;
            System.Threading.SynchronizationContext.SetSynchronizationContext(NotAPump.Instance);
            try
            {
                var settled = new JSPromise((resolve, _) => resolve(JSUndefined.Value));
                settled.AddReactions((in Arguments _) =>
                {
                    if (Environment.CurrentManagedThreadId != thread)
                        return JSUndefined.Value;

                    try
                    {
                        job();
                    }
                    catch (Exception ex)
                    {
                        RenderLogger.LogError(LogCategory.JavaScript, "ScriptEngine.EngineJobs", $"Job error: {ex.Message}", ex);
                    }

                    return JSUndefined.Value;
                }, null!);
            }
            finally
            {
                System.Threading.SynchronizationContext.SetSynchronizationContext(previous);
            }

            return true;
        }

        private sealed class JobQueue(System.Threading.SynchronizationContext pump)
            : System.Threading.SynchronizationContext, IJSJobPump
        {
            public override void Post(System.Threading.SendOrPostCallback d, object? state) => pump.Post(d, state);

            public override void Send(System.Threading.SendOrPostCallback d, object? state) => pump.Send(d, state);

            // A copy must post to the same pump, not to a context nothing drains.
            public override System.Threading.SynchronizationContext CreateCopy() => this;
        }

        // Current while the reaction above is queued, so the promise captures a context that is not a
        // pump and the engine takes its own queue.
        private sealed class NotAPump : System.Threading.SynchronizationContext
        {
            public static readonly NotAPump Instance = new();
        }
    }
}
