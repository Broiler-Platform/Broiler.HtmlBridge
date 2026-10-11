using System.Collections.Concurrent;
using System.Net;
using Broiler.HtmlBridge.Dom;
using Broiler.HtmlBridge.Dom.Runtime;
using Broiler.Net.Http;

namespace Broiler.HtmlBridge.Tests;

public class AsyncFetchTests
{
    private const string Html = "<!doctype html><html><body><div id=\"out\"></div></body></html>";
    private const string Helpers = "function done(v) { document.getElementById('out').textContent = v; }";

    private sealed class GatedHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ConcurrentQueue<string> Started = new();
        public int Cancelled;
        public int DisposedBodies;
        public bool IgnoreCancellation;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Started.Enqueue(request.RequestUri!.AbsolutePath);
            try { await Release.Task.WaitAsync(IgnoreCancellation ? CancellationToken.None : token).ConfigureAwait(false); }
            catch (OperationCanceledException) { Interlocked.Increment(ref Cancelled); throw; }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new TrackedContent(request.RequestUri.AbsolutePath, () => Interlocked.Increment(ref DisposedBodies))
            };
        }

        private sealed class TrackedContent(string body, Action onDispose) : StringContent(body)
        {
            protected override void Dispose(bool disposing)
            {
                if (disposing) onDispose();
                base.Dispose(disposing);
            }
        }
    }

    private static ScriptEngine Engine(BrowserNetworkSession network) =>
        new(new DomBridgeFactory(new DomBridgeSessionOptions { Network = network, Cookies = network }));

    private static InteractiveSession Start(BrowserNetworkSession network, string script) =>
        Engine(network).ExecuteInteractive([Helpers, script], [], Html, "https://example.test/page")!;

    [Theory]
    [InlineData("fetch('/a').then(r => r.text()).then(done); fetch('/b').then(r => r.text());")]
    [InlineData("var a = new XMLHttpRequest(), b = new XMLHttpRequest(); a.open('GET','/a'); b.open('GET','/b'); a.onload = () => done(a.responseText); a.send(); b.send();")]
    [InlineData("done(navigator.sendBeacon('/a') && navigator.sendBeacon('/b') ? 'queued' : 'failed');")]
    public void CallsReturnAndOverlapWhileResponsesArePending(string script)
    {
        using var handler = new GatedHandler();
        using var network = new BrowserNetworkSession(new() { Handler = handler });
        using var session = Start(network, script + "done('returned'); setTimeout(() => done('timer'), 0);");
        Assert.NotNull(session);
        Assert.Equal(["/a", "/b"], handler.Started.ToArray());
        Assert.Equal(0, handler.Cancelled);
        Assert.Equal("returned", PageProbe.OutOf(session.CurrentHtml()));
        Assert.True(session.HasWorkDueInLoadWindow);
        Assert.True(session.HasPendingWork);
        Assert.Equal("timer", PageProbe.OutOf(session.Step()!));
        handler.Release.SetResult();
        session.SettleLoadWindow();
        Assert.Equal(2, handler.DisposedBodies);
        Assert.False(session.HasWorkDueInLoadWindow);
    }

    [Fact]
    public void AbortAfterDispatchCancelsTransportAndPreservesReason()
    {
        using var handler = new GatedHandler();
        using var network = new BrowserNetworkSession(new() { Handler = handler });
        var html = Engine(network).Execute([Helpers, """
            var controller = new AbortController(), reason = { custom: true };
            fetch('/a', { signal: controller.signal }).then(() => done('loaded'), e => done(e === reason ? 'aborted' : 'wrong'));
            setTimeout(() => controller.abort(reason), 20);
            """], Html, "https://example.test/page");
        Assert.Equal("aborted", PageProbe.OutOf(html!));
        Assert.Equal(1, handler.Cancelled);
    }

    [Theory]
    [InlineData("xhr.timeout = 20;", "timeout")]
    [InlineData("setTimeout(() => xhr.abort(), 20);", "abort")]
    public void XhrTimeoutAndAbortCancelTransport(string setup, string expected)
    {
        using var handler = new GatedHandler();
        using var network = new BrowserNetworkSession(new() { Handler = handler });
        var script = "var xhr = new XMLHttpRequest(); xhr.open('GET','/a');" +
            "xhr.onload = () => done('load'); xhr.onerror = () => done('error');" +
            "xhr.onabort = () => done('abort'); xhr.ontimeout = () => done('timeout');" + setup + "xhr.send();";
        var html = Engine(network).Execute([Helpers, script], Html, "https://example.test/page");
        Assert.Equal(expected, PageProbe.OutOf(html!));
        Assert.Equal(1, handler.Cancelled);
    }

    [Fact]
    public void ReopeningXhrCancelsOldRequestAndIgnoresItsCompletion()
    {
        using var handler = new GatedHandler();
        using var network = new BrowserNetworkSession(new() { Handler = handler });
        using var session = Start(network, """
            var xhr = new XMLHttpRequest(); xhr.open('GET','/old'); xhr.send();
            xhr.open('GET','/new'); xhr.onload = () => done(xhr.responseText);
            xhr.onerror = () => done('error'); xhr.send();
            """);
        Assert.True(SpinWait.SpinUntil(() => handler.Cancelled == 1, TimeSpan.FromSeconds(2)));
        handler.Release.SetResult();
        Assert.Equal("/new", PageProbe.OutOf(session.SettleLoadWindow()));
    }

    [Theory]
    [InlineData("fetch('/a').then(() => done('late'));")]
    [InlineData("navigator.sendBeacon('/a');")]
    public void SessionDisposalCancelsPendingRequests(string script)
    {
        using var handler = new GatedHandler();
        using var network = new BrowserNetworkSession(new() { Handler = handler });
        var session = Start(network, script);
        Assert.Single(handler.Started);
        session.Dispose();
        Assert.True(SpinWait.SpinUntil(() => handler.Cancelled == 1, TimeSpan.FromSeconds(2)));
        handler.Release.TrySetResult();
    }

    [Fact]
    public void SynchronousXhrHasResponseBeforeSendReturns()
    {
        using var handler = new GatedHandler();
        handler.Release.SetResult();
        using var network = new BrowserNetworkSession(new() { Handler = handler });
        using var session = Start(network, "var x = new XMLHttpRequest(); x.open('GET','/sync',false); x.send(); done(x.readyState + ':' + x.responseText);");
        Assert.Equal("4:/sync", PageProbe.OutOf(session.CurrentHtml()));
    }

    [Fact]
    public void LateResponseAfterDisposalStillReleasesTransportResources()
    {
        using var handler = new GatedHandler { IgnoreCancellation = true };
        using var network = new BrowserNetworkSession(new() { Handler = handler });
        var session = Start(network, "fetch('/a').then(() => done('late'));");
        session.Dispose();
        handler.Release.SetResult();
        Assert.True(SpinWait.SpinUntil(() => handler.DisposedBodies == 1, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void SuccessfulAsyncXhrClearsItsTimeout()
    {
        using var handler = new GatedHandler();
        using var network = new BrowserNetworkSession(new() { Handler = handler });
        using var session = Start(network, """
            var xhr = new XMLHttpRequest(); xhr.open('GET','/a'); xhr.timeout = 1000;
            xhr.onload = () => done('loaded'); xhr.ontimeout = () => done('timeout'); xhr.send();
            """);
        handler.Release.SetResult();
        Assert.Equal("loaded", PageProbe.OutOf(session.SettleLoadWindow()));
        Assert.Equal(0, handler.Cancelled);
    }

    [Fact]
    public void CompleteWaitsForNetworkAndDeliversPromiseReactions()
    {
        using var handler = new GatedHandler();
        using var network = new BrowserNetworkSession(new() { Handler = handler });
        using var session = Start(network, "fetch('/a').then(r => r.text()).then(done);");
        Assert.True(session.HasPendingWork);
        handler.Release.SetResult();
        Assert.Equal("/a", PageProbe.OutOf(session.Complete()));
        Assert.False(session.HasPendingWork);
    }

    [Fact]
    public void NetworkCompletionRunsOnlyWhenOwnerDrainsEventLoop()
    {
        var loop = new BrowserEventLoop(() => null);
        var owner = Environment.CurrentManagedThreadId;
        var completionThread = 0;
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        loop.StartAsync(_ => gate.Task, (_, _) => completionThread = Environment.CurrentManagedThreadId);
        gate.SetResult(1);
        Assert.True(SpinWait.SpinUntil(() => loop.HasWorkDueNow, TimeSpan.FromSeconds(2)));
        Assert.Equal(0, completionThread);
        Assert.True(loop.DrainStep(null));
        Assert.Equal(owner, completionThread);
    }

    [Fact]
    public void ClearingEventLoopDropsLateCompletionAndCancelsItsToken()
    {
        var loop = new BrowserEventLoop(() => null);
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        var called = false;
        loop.StartAsync(ct => { token = ct; return gate.Task; }, (_, _) => called = true);
        loop.Clear();
        Assert.True(token.IsCancellationRequested);
        gate.SetResult(1);
        Assert.False(loop.HasAsyncWork);
        Assert.False(loop.DrainStep(null));
        Assert.False(called);
    }

    private sealed class DeferredStreamContent(Func<Stream, CancellationToken, Task> producer, Action? onDispose = null) : HttpContent
    {
        private readonly System.IO.Pipelines.Pipe _pipe = new();
        private int _started;

        private void StartProducing(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
                return;

            _ = Task.Run(async () =>
            {
                var writerStream = _pipe.Writer.AsStream();
                try
                {
                    await producer(writerStream, cancellationToken).ConfigureAwait(false);
                    await _pipe.Writer.CompleteAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    await _pipe.Writer.CompleteAsync(ex).ConfigureAwait(false);
                }
            }, cancellationToken);
        }

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
        {
            StartProducing(cancellationToken);
            return _pipe.Reader.AsStream();
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            StartProducing(CancellationToken.None);
            return Task.FromResult<Stream>(_pipe.Reader.AsStream());
        }

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
        {
            StartProducing(cancellationToken);
            return Task.FromResult<Stream>(_pipe.Reader.AsStream());
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            producer(stream, CancellationToken.None);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            producer(stream, cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) onDispose?.Invoke();
            base.Dispose(disposing);
        }
    }

    private sealed class StreamingHandler : HttpMessageHandler
    {
        public readonly TaskCompletionSource ReleaseBody = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Cancelled;
        public int DisposedBodies;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var content = new DeferredStreamContent(async (stream, ct) =>
            {
                try
                {
                    await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("chunk1,"), ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                    await ReleaseBody.Task.WaitAsync(ct).ConfigureAwait(false);
                    await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes("chunk2"), ct).ConfigureAwait(false);
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref Cancelled);
                    throw;
                }
            }, onDispose: () => Interlocked.Increment(ref DisposedBodies));

            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = content
            });
        }
    }

    [Fact]
    public void FetchSettlesAtHeadersBeforeStreamingBodyArrives()
    {
        using var handler = new StreamingHandler();
        using var network = new BrowserNetworkSession(new() { Handler = handler });
        using var session = Start(network, """
            fetch('/stream').then(r => done('status:' + r.status));
            """);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            session.Step();
            return PageProbe.OutOf(session.CurrentHtml()) == "status:200";
        }, TimeSpan.FromSeconds(2)));
        Assert.Equal("status:200", PageProbe.OutOf(session.CurrentHtml()));
        handler.ReleaseBody.SetResult();
        session.Dispose();
        Assert.Equal(1, handler.DisposedBodies);
    }

    [Fact]
    public void StreamingBodyTextWaitsForStreamCompletion()
    {
        using var handler = new StreamingHandler();
        using var network = new BrowserNetworkSession(new() { Handler = handler });
        using var session = Start(network, """
            fetch('/stream')
                .then(r => { done('headers'); return r.text(); })
                .then(text => done(text));
            """);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            session.Step();
            return PageProbe.OutOf(session.CurrentHtml()) == "headers";
        }, TimeSpan.FromSeconds(2)));
        Assert.Equal("headers", PageProbe.OutOf(session.CurrentHtml()));
        handler.ReleaseBody.SetResult();
        Assert.Equal("chunk1,chunk2", PageProbe.OutOf(session.Complete()));
    }

    [Fact]
    public void StreamReaderStreamsChunksAsTheyArrive()
    {
        using var handler = new StreamingHandler();
        using var network = new BrowserNetworkSession(new() { Handler = handler });
        using var session = Start(network, """
            var chunks = [];
            fetch('/stream').then(r => {
                var reader = r.body.getReader();
                function readNext() {
                    return reader.read().then(({done: d, value: v}) => {
                        if (d) { done(chunks.join('')); return; }
                        chunks.push(new TextDecoder().decode(v));
                        done('read:' + chunks.join(''));
                        return readNext();
                    });
                }
                return readNext();
            });
            """);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            session.Step();
            return PageProbe.OutOf(session.CurrentHtml()) == "read:chunk1,";
        }, TimeSpan.FromSeconds(2)));
        Assert.Equal("read:chunk1,", PageProbe.OutOf(session.CurrentHtml()));
        handler.ReleaseBody.SetResult();
        Assert.Equal("chunk1,chunk2", PageProbe.OutOf(session.Complete()));
    }

    [Fact]
    public void AbortingDuringBodyStreamCancelsUnderlyingTransport()
    {
        using var handler = new StreamingHandler();
        using var network = new BrowserNetworkSession(new() { Handler = handler });
        using var session = Start(network, """
            var controller = new AbortController();
            fetch('/stream', { signal: controller.signal })
                .then(r => {
                    done('headers');
                    setTimeout(() => controller.abort(), 10);
                    return r.text();
                })
                .then(() => done('loaded'), e => done('aborted:' + e.name));
            """);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            session.Step();
            return PageProbe.OutOf(session.CurrentHtml()) == "headers";
        }, TimeSpan.FromSeconds(2)));
        session.SettleLoadWindow();
        Assert.Equal("aborted:AbortError", PageProbe.OutOf(session.CurrentHtml()));
        Assert.Equal(1, handler.Cancelled);
    }

    [Fact]
    public void StreamReaderCancelCancelsUnderlyingTransport()
    {
        using var handler = new StreamingHandler();
        using var network = new BrowserNetworkSession(new() { Handler = handler });
        using var session = Start(network, """
            fetch('/stream').then(r => {
                var reader = r.body.getReader();
                return reader.read().then(() => reader.cancel());
            }).then(() => done('cancelled'));
            """);
        Assert.True(SpinWait.SpinUntil(() =>
        {
            session.Step();
            return PageProbe.OutOf(session.CurrentHtml()) == "cancelled";
        }, TimeSpan.FromSeconds(2)));
        Assert.True(SpinWait.SpinUntil(() => handler.Cancelled == 1 || handler.DisposedBodies == 1, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void CloningStreamingResponseAllowsReadingBothBranches()
    {
        using var handler = new StreamingHandler();
        using var network = new BrowserNetworkSession(new() { Handler = handler });
        using var session = Start(network, """
            fetch('/stream').then(r => {
                var c = r.clone();
                return Promise.all([r.text(), c.text()]);
            }).then(([t1, t2]) => done(t1 + '|' + t2));
            """);
        handler.ReleaseBody.SetResult();
        Assert.Equal("chunk1,chunk2|chunk1,chunk2", PageProbe.OutOf(session.Complete()));
    }
}

