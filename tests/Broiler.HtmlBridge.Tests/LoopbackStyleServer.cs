using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// An HTTP origin on the loopback interface that serves a fixed map of paths to stylesheet bodies and
/// records the path of every request it receives, for tests that have to show a stylesheet was never
/// asked for rather than only that its rules are missing.
/// <para>
/// <b>Why a request log.</b> A blocked <c>@import</c> and an import whose fetch failed look the same in
/// the render projection: no rules. Only the server can tell "refused before a request was made" from
/// "requested and then dropped", and a Content-Security-Policy check that runs after the fetch has
/// already leaked the request. The stylesheet fetch behind the render projection is synchronous, so the
/// log is complete by the time <c>ScriptEngine.Execute</c> returns.
/// </para>
/// <para>
/// <b>Why not <c>SubDocumentContentSecurityPolicyTests</c>' loopback origin.</b> That one is private,
/// serves one body for every path and keeps no log. This is the same socket approach: a raw
/// <see cref="TcpListener"/> rather than <c>HttpListener</c>, which needs a URL reservation on Windows
/// for anything but an elevated process, and an ephemeral port chosen by the OS so parallel test
/// classes cannot collide. A path missing from the map answers <c>404</c>, which the loader reports as
/// a failed fetch.
/// </para>
/// </summary>
internal sealed class LoopbackStyleServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly IReadOnlyDictionary<string, string> _bodies;
    private readonly ConcurrentQueue<string> _requests = new();

    /// <param name="bodies">
    /// Request path (starting with <c>/</c>, unescaped) to the <c>text/css</c> body served for it.
    /// </param>
    public LoopbackStyleServer(IReadOnlyDictionary<string, string> bodies)
    {
        _bodies = new Dictionary<string, string>(bodies, StringComparer.Ordinal);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Origin = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
        _ = Task.Run(AcceptAsync);
    }

    /// <summary>The origin, <c>http://127.0.0.1:PORT</c>, with no trailing slash.</summary>
    public string Origin { get; }

    /// <summary>A URL on this origin for a document, so the policy's <c>'self'</c> is this server.</summary>
    public string PageUrl => Origin + "/page";

    /// <summary>
    /// The distinct request paths received so far, sorted ordinally. Distinct because every render
    /// projection fetches its imports again, so the count says how often a projection was built and
    /// nothing about policy.
    /// </summary>
    public string[] RequestedPaths() =>
        _requests.Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray();

    private async Task AcceptAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stopping.Token);
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { return; }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();

                // Drain the request line and headers before answering: a response written while the
                // client is still sending can surface as a connection reset rather than a response.
                var received = new List<byte>();
                var buffer = new byte[4096];
                while (!EndsHeaders(received))
                {
                    var read = await stream.ReadAsync(buffer, _stopping.Token);
                    if (read <= 0)
                        return;
                    received.AddRange(buffer.AsSpan(0, read).ToArray());
                }

                var requestLine = Encoding.ASCII.GetString(received.ToArray()).Split("\r\n")[0];
                var parts = requestLine.Split(' ');
                if (parts.Length < 2)
                    return;

                var path = Uri.UnescapeDataString(parts[1]);
                _requests.Enqueue(path);

                var found = _bodies.TryGetValue(path, out var body);
                var payload = Encoding.UTF8.GetBytes(found ? body! : string.Empty);
                var header = Encoding.ASCII.GetBytes(
                    $"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\n" +
                    "Content-Type: text/css\r\n" +
                    $"Content-Length: {payload.Length}\r\n" +
                    "Connection: close\r\n\r\n");

                await stream.WriteAsync(header, _stopping.Token);
                await stream.WriteAsync(payload, _stopping.Token);
                await stream.FlushAsync(_stopping.Token);
            }
            catch (Exception) { /* the client hung up; nothing here is worth failing a test over */ }
        }
    }

    private static bool EndsHeaders(List<byte> received)
    {
        var n = received.Count;
        return n >= 4 &&
               received[n - 4] == '\r' && received[n - 3] == '\n' &&
               received[n - 2] == '\r' && received[n - 1] == '\n';
    }

    public void Dispose()
    {
        _stopping.Cancel();
        _listener.Stop();
        _stopping.Dispose();
    }
}
