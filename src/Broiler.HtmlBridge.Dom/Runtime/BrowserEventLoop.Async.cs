using System.Diagnostics;

namespace Broiler.HtmlBridge.Dom.Runtime;

internal sealed partial class BrowserEventLoop
{
    private readonly object _asyncGate = new();
    private readonly Dictionary<CancellationTokenSource, Action?> _asyncRequests = [];
    private long _asyncGeneration;
    private long _asyncClockStart;
    private double _asyncClockBase;

    internal bool HasAsyncWork
    {
        get { lock (_asyncGate) return _asyncRequests.Count != 0; }
    }

    // Real I/O must race timers in real time. With no I/O, retain the deterministic
    // virtual clock used by captures and animation tests.
    private double CurrentTime
    {
        get
        {
            lock (_asyncGate)
            {
                UpdateAsyncClock();
                return _virtualNowMs;
            }
        }
    }

    private void UpdateAsyncClock()
    {
        if (_asyncRequests.Count != 0)
            _virtualNowMs = Math.Max(_virtualNowMs,
                _asyncClockBase + Stopwatch.GetElapsedTime(_asyncClockStart).TotalMilliseconds);
    }

    /// <summary>Runs only CLR I/O off-thread; delivers its outcome on the owning event loop.
    /// Work must dispose transport resources before returning its result.</summary>
    internal Action StartAsync<T>(Func<CancellationToken, Task<T>> work, Action<T, Exception?> complete,
        Action? abandoned = null)
    {
        var cancellation = new CancellationTokenSource();
        long generation;
        lock (_asyncGate)
        {
            generation = _asyncGeneration;
            if (_asyncRequests.Count == 0)
            {
                _asyncClockBase = _virtualNowMs;
                _asyncClockStart = Stopwatch.GetTimestamp();
            }
            _asyncRequests.Add(cancellation, abandoned);
        }
        _ = Run();
        return Cancel;

        void Cancel()
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        async Task Run()
        {
            T result = default!;
            Exception? error = null;
            try { result = await work(cancellation.Token).ConfigureAwait(false); }
            catch (Exception ex) { error = ex; }
            lock (_asyncGate)
            {
                if (generation == _asyncGeneration)
                {
                    QueueTask(() =>
                    {
                        lock (_asyncGate)
                        {
                            if (generation != _asyncGeneration)
                            {
                                (result as IDisposable)?.Dispose();
                                return;
                            }
                            UpdateAsyncClock();
                            _asyncRequests.Remove(cancellation);
                        }
                        complete(result, error);
                    });
                    Monitor.PulseAll(_asyncGate);
                }
                else
                {
                    (result as IDisposable)?.Dispose();
                }
            }
            // The returned cancellation action is safe even after completion.
            cancellation.Dispose();
        }
    }

    internal bool AwaitAsyncWork(TimeSpan timeout)
    {
        lock (_asyncGate)
        {
            if (_asyncRequests.Count == 0 || HasWorkDueNow) return true;
            // Wake for a network result, a due page timer, or host cancellation checks.
            var milliseconds = Math.Min(50, timeout.TotalMilliseconds);
            var now = CurrentTime;
            foreach (var timer in _timers.Values)
                milliseconds = Math.Min(milliseconds, Math.Max(0, timer.Deadline - now));
            if (milliseconds > 0)
                Monitor.Wait(_asyncGate, TimeSpan.FromMilliseconds(milliseconds));
            return _asyncRequests.Count == 0 || HasWorkDueNow;
        }
    }

    private void ClearAsyncWork()
    {
        KeyValuePair<CancellationTokenSource, Action?>[] pending;
        lock (_asyncGate)
        {
            ++_asyncGeneration;
            pending = [.. _asyncRequests];
            _asyncRequests.Clear();
            // A completion cannot enqueue between generation invalidation and this clear.
            _timers.Clear();
            Monitor.PulseAll(_asyncGate);
        }
        foreach (var (cancellation, abandoned) in pending)
        {
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            // Teardown runs on the owner thread, before the realm is released.
            abandoned?.Invoke();
        }
    }
}
