using Broiler.HtmlBridge;
using Broiler.Net.Http;
using Reply = Broiler.HtmlBridge.Tests.LoopbackCookieServer.Reply;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// <c>MessagePort</c>s between a page and its worker: a port transferred either way stays entangled
/// with its peer across the two threads, carries its undelivered messages along, and leaves an inert
/// object behind.
/// </summary>
/// <remarks>
/// <para>
/// <b>A port could not leave the realm it was made in.</b> A worker's ports were a script of its own
/// and the page's were the bridge's, and the worker's transfer list took only buffers: a worker that
/// handed the page a port got a <c>DataCloneError</c>, and so did a page handing one to its worker.
/// reCAPTCHA's worker hands its page a port of its own channel and talks through it, and stopped there.
/// </para>
/// <para>
/// The page is on <c>localhost</c>, and its worker script is served at <c>/worker.js</c>. Each page
/// writes what it heard into <c>#out</c>; the settle waits for the worker.
/// </para>
/// </remarks>
public class WorkerPortTests
{
    private static string Run(string workerScript, string pageScript, string body = "", string bodyAttributes = "")
    {
        using var server = new LoopbackCookieServer();
        server.Map("/worker.js", new Reply(ContentType: "text/javascript", Body: workerScript));

        using var profile = new BrowserNetworkSession(new BrowserNetworkSessionOptions { Timeout = TimeSpan.FromSeconds(20) });
        var engine = new ScriptEngine(new DomBridgeFactory(new DomBridgeSessionOptions { Network = profile, Cookies = profile }));
        using var session = engine.ExecuteInteractive(
            ["var out = document.getElementById('out');" + pageScript],
            [],
            $"<html><body{bodyAttributes}><div id=\"out\">waiting</div>{body}</body></html>",
            server.LocalhostUrl("/page"));

        return PageProbe.OutOf(session!.SettleLoadWindow(), decode: true);
    }

    /// <summary>
    /// reCAPTCHA's handshake: the worker hands the page a port of a channel of its own, and the two
    /// talk through it. The worker's <c>postMessage</c> threw a <c>DataCloneError</c>.
    /// </summary>
    [Fact]
    public void AWorkerHandsThePageAPortAndTalksThroughIt()
    {
        Assert.Equal(
            "here is a port|1||null|worker got hello",
            Run(
                "var channel = new MessageChannel();" +
                "channel.port1.onmessage = function (e) { channel.port1.postMessage('worker got ' + e.data); };" +
                "postMessage('here is a port', [channel.port2]);",
                "var worker = new Worker('/worker.js');" +
                "worker.onmessage = function (e) {" +
                "  var port = e.ports[0];" +
                "  port.onmessage = function (m) { out.textContent = [e.data, e.ports.length, e.origin, String(e.source), m.data].join('|'); };" +
                "  port.postMessage('hello');" +
                "};"));
    }

    /// <summary>
    /// A page hands its worker one port of a channel and hears back through the other. The page's
    /// <c>postMessage</c> threw a <c>DataCloneError</c>.
    /// </summary>
    [Fact]
    public void APageHandsItsWorkerAPortAndHearsBackThroughIt()
    {
        Assert.Equal(
            "worker: take this 1",
            Run(
                "onmessage = function (e) { e.ports[0].postMessage('worker: ' + e.data + ' ' + e.ports.length); };",
                "var channel = new MessageChannel();" +
                "channel.port1.onmessage = function (e) { out.textContent = e.data; };" +
                "new Worker('/worker.js').postMessage('take this', [channel.port2]);"));
    }

    /// <summary>Messages through a port that crossed to the worker arrive in the order they were posted, both ways.</summary>
    [Fact]
    public void MessagesThroughATransferredPortArriveInOrder()
    {
        Assert.Equal(
            string.Join(",", Enumerable.Range(0, 40)),
            Run(
                "onmessage = function (e) { var port = e.ports[0]; port.onmessage = function (m) { port.postMessage(m.data); }; };",
                "var channel = new MessageChannel(); var echoed = [];" +
                "channel.port1.onmessage = function (e) { echoed.push(e.data); if (echoed.length === 40) out.textContent = echoed.join(','); };" +
                "new Worker('/worker.js').postMessage('echo', [channel.port2]);" +
                "for (var i = 0; i < 40; i++) channel.port1.postMessage(i);"));
    }

    /// <summary>
    /// A port carries the messages it had not dispatched yet to the worker: ones already queued at
    /// it, and ones still on their way when it was transferred, ahead of what comes after.
    /// </summary>
    [Theory]
    [InlineData("setTimeout(function () { send(); }, 10);")]
    [InlineData("send();")]
    public void APortsUndeliveredMessagesGoWithItToTheWorker(string whenSent)
    {
        Assert.Equal(
            "a,b,c",
            Run(
                "onmessage = function (e) { var got = []; e.ports[0].onmessage = function (m) { got.push(m.data); if (got.length === 3) postMessage(got.join(',')); }; };",
                "var channel = new MessageChannel(); var worker = new Worker('/worker.js');" +
                "worker.onmessage = function (e) { out.textContent = e.data; };" +
                "channel.port1.postMessage('a'); channel.port1.postMessage('b');" +
                "function send() { worker.postMessage('queued', [channel.port2]); channel.port1.postMessage('c'); }" +
                whenSent));
    }

    /// <summary>
    /// The page's object for a port it transferred is left behind inert: posting through it does
    /// nothing, and it cannot be transferred again. The port's peer talks to the worker's port.
    /// </summary>
    [Fact]
    public void ATransferredPortIsLeftBehindInert()
    {
        Assert.Equal(
            "DataCloneError|nothing|worker heard from port1",
            Run(
                "onmessage = function (e) { e.ports[0].onmessage = function (m) { postMessage('worker heard ' + m.data); }; };",
                "var channel = new MessageChannel(); var worker = new Worker('/worker.js');" +
                "var refused = 'no error'; var port1Heard = 'nothing';" +
                "channel.port1.onmessage = function (e) { port1Heard = e.data; };" +
                "worker.onmessage = function (e) { out.textContent = [refused, port1Heard, e.data].join('|'); };" +
                "worker.postMessage('port', [channel.port2]);" +
                "channel.port2.postMessage('from the old object');" +
                "try { worker.postMessage('again', [channel.port2]); } catch (e) { refused = e.name; }" +
                "channel.port1.postMessage('from port1');"));
    }

    /// <summary>Both ports of a channel can go to the worker, which then talks between them.</summary>
    [Fact]
    public void BothPortsOfAChannelCanGoToTheWorker()
    {
        Assert.Equal(
            "through the pair: ping",
            Run(
                "onmessage = function (e) { e.ports[0].onmessage = function (m) { postMessage('through the pair: ' + m.data); }; e.ports[1].postMessage('ping'); };",
                "var channel = new MessageChannel(); var worker = new Worker('/worker.js');" +
                "worker.onmessage = function (e) { out.textContent = e.data; };" +
                "worker.postMessage('pair', [channel.port1, channel.port2]);"));
    }

    /// <summary>
    /// A port goes to the worker and comes back: the page's new object for it is entangled with the
    /// peer the page kept.
    /// </summary>
    [Fact]
    public void APortSentToTheWorkerCanComeBack()
    {
        Assert.Equal(
            "round trip: x|false",
            Run(
                "onmessage = function (e) { postMessage('back', [e.ports[0]]); };",
                "var channel = new MessageChannel(); var worker = new Worker('/worker.js');" +
                "channel.port1.onmessage = function (e) { out.textContent = 'round trip: ' + e.data + '|' + (returned === channel.port2); };" +
                "var returned;" +
                "worker.onmessage = function (e) { returned = e.ports[0]; returned.postMessage('x'); };" +
                "worker.postMessage('go', [channel.port2]);"));
    }

    /// <summary>
    /// A page that posts to its worker through a port and gives up after a timeout hears the answer
    /// first, as it does when it posts to the worker itself: a message for one of the worker's ports
    /// is work the page waits on.
    /// </summary>
    [Fact]
    public void APageWaitsForItsWorkersAnswerThroughAPort()
    {
        Assert.Equal(
            "answered: pong",
            Run(
                "onmessage = function (e) { var port = e.ports[0]; port.onmessage = function () { var until = Date.now() + 200; while (Date.now() < until) { } port.postMessage('pong'); }; };",
                "var channel = new MessageChannel(); var settled = false;" +
                "channel.port1.onmessage = function (e) { if (!settled) { settled = true; out.textContent = 'answered: ' + e.data; } };" +
                "new Worker('/worker.js').postMessage('port', [channel.port2]);" +
                "channel.port1.postMessage('ping');" +
                "setTimeout(function () { if (!settled) { settled = true; out.textContent = 'gave up'; } }, 5000);"));
    }

    /// <summary>
    /// A message between two ports of the worker is a copy, and <c>MessagePort</c> is there to be
    /// detected but not constructed.
    /// </summary>
    [Fact]
    public void AWorkersOwnChannelCopiesItsMessages()
    {
        Assert.Equal(
            "true|1|function|TypeError",
            Run(
                "var threw = 'none'; try { new MessagePort(); } catch (e) { threw = e.name; }" +
                "var channel = new MessageChannel(); var sent = { a: 1 };" +
                "channel.port1.onmessage = function (e) { postMessage([e.data !== sent, e.data.a, typeof MessagePort, threw].join('|')); };" +
                "channel.port2.postMessage(sent); sent.a = 2;",
                "new Worker('/worker.js').onmessage = function (e) { out.textContent = e.data; };"));
    }

    /// <summary>
    /// A frame's worker is the frame's: its messages run as the frame's script, so its listener sees
    /// the frame's document. It saw the page's.
    /// </summary>
    /// <remarks>
    /// The frame writes into the page's <c>#out</c> through <c>pageOut</c>, which the page's script
    /// sets: every document shares one global object, so a frame's <c>parent.document</c> is its own.
    /// </remarks>
    [Fact]
    public void AFramesWorkerTalksToTheFrame()
    {
        Assert.Equal(
            "frame-body|framed",
            Run(
                "postMessage('framed');",
                "var pageOut = out; document.getElementById('f').contentWindow;",
                "<iframe id=\"f\" srcdoc=\"<body id='frame-body'><script>" +
                "new Worker('/worker.js').onmessage = function (e) { pageOut.textContent = [document.body.id, e.data].join('|'); };" +
                "</script></body>\"></iframe>",
                bodyAttributes: " id=\"page-body\""));
    }
}
