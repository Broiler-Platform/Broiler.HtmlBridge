using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// An HTTP/1.1 server on the loopback interface for the profile-network tests: each path answers a
/// scripted response (status, content type, body, <c>Set-Cookie</c> fields, a redirect, any other
/// header), and every request is logged with its method, the host it named, every header field it
/// carried — the <c>Cookie</c> header above all — and its body.
/// <para>
/// <b>Two sites on one port.</b> The server listens on <c>127.0.0.1</c> and, when it can, on
/// <c>[::1]</c> at the same port, so <c>http://127.0.0.1:P</c> and <c>http://localhost:P</c> reach it
/// both. They are different sites (an IP address is its own site; <c>localhost</c> is a public suffix
/// under the Public Suffix List's implicit rule), which is what the cross-site cases need. The
/// Broiler.Net transport pins <c>localhost</c> to the loopback addresses, never DNS or a proxy.
/// </para>
/// <para>
/// Same socket approach as <see cref="LoopbackStyleServer"/>: a raw <see cref="TcpListener"/>, which
/// needs no URL reservation on Windows, and an ephemeral port chosen by the OS so parallel test
/// classes cannot collide. Every response closes its connection.
/// </para>
/// </summary>
internal sealed class LoopbackCookieServer : IDisposable
{
    private readonly List<TcpListener> _listeners = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<string, Func<Request, Reply>> _routes = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Request> _requests = new();

    public LoopbackCookieServer()
    {
        Port = BindLoopback(_listeners);

        foreach (var listener in _listeners)
            _ = Task.Run(() => AcceptAsync(listener));
    }

    /// <summary>
    /// Listens on 127.0.0.1 at an ephemeral port and on ::1 at the same port, and answers the port.
    /// </summary>
    /// <remarks>
    /// Broiler.Net dials <c>localhost</c> at ::1 first and keeps whichever connection succeeds, so a ::1
    /// port held by another process would receive this server's <c>localhost</c> requests. The OS hands
    /// out IPv4 and IPv6 ports independently, so a taken ::1 port means another port pair, not an
    /// IPv4-only server; only a machine without IPv6 loopback is served over 127.0.0.1 alone
    /// (Broiler.Net falls back to it after a short delay).
    /// </remarks>
    internal static int BindLoopback(List<TcpListener> listeners)
    {
        const int attempts = 20;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (TryBindLoopbackPair(0, listeners, out var port))
                return port;
        }

        throw new InvalidOperationException(
            $"No ephemeral port was free on both 127.0.0.1 and [::1] after {attempts} attempts; localhost requests could not be routed to this server.");
    }

    /// <summary>
    /// One attempt of <see cref="BindLoopback"/>: 127.0.0.1 at <paramref name="ipv4Port"/> (0 for an
    /// ephemeral one), then ::1 at the same port. False, holding nothing, when another listener already
    /// has ::1 at that port.
    /// </summary>
    internal static bool TryBindLoopbackPair(int ipv4Port, List<TcpListener> listeners, out int port)
    {
        var ipv4 = new TcpListener(IPAddress.Loopback, ipv4Port);
        ipv4.Start();
        port = ((IPEndPoint)ipv4.LocalEndpoint).Port;

        if (!Socket.OSSupportsIPv6)
        {
            listeners.Add(ipv4);
            return true;
        }

        var ipv6 = new TcpListener(IPAddress.IPv6Loopback, port);
        try
        {
            ipv6.Start();
            listeners.Add(ipv4);
            listeners.Add(ipv6);
            return true;
        }
        catch (SocketException error) when (error.SocketErrorCode is SocketError.AddressNotAvailable or SocketError.AddressFamilyNotSupported)
        {
            // No IPv6 loopback on this machine: localhost reaches the IPv4 listener.
            listeners.Add(ipv4);
            return true;
        }
        catch (SocketException error) when (error.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            // Another process holds [::1]:port and would answer this server's localhost requests.
            ipv4.Stop();
            return false;
        }
    }

    /// <summary>A request as the server received it.</summary>
    /// <param name="Fields">Every header field in the order received, repeated names included.</param>
    /// <param name="Body">The body, decoded as UTF-8; empty for none.</param>
    internal sealed record Request(
        string Method,
        string Path,
        string Host,
        string? Cookie,
        string? Origin,
        IReadOnlyList<(string Name, string Value)>? Fields = null,
        string Body = "")
    {
        /// <summary>Every value of <paramref name="name"/>, joined with ", "; null when the request had none.</summary>
        public string? Header(string name)
        {
            var values = (Fields ?? [])
                .Where(field => string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))
                .Select(field => field.Value)
                .ToArray();
            return values.Length == 0 ? null : string.Join(", ", values);
        }

        /// <summary>How many header fields named <paramref name="name"/> the request carried.</summary>
        public int CountOf(string name) =>
            (Fields ?? []).Count(field => string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A scripted response.</summary>
    internal sealed record Reply(
        int Status = 200,
        string ContentType = "text/html",
        string Body = "",
        IReadOnlyList<string>? SetCookies = null,
        string? Location = null,
        IReadOnlyList<(string Name, string Value)>? Headers = null);

    public int Port { get; }

    /// <summary><c>http://127.0.0.1:PORT</c> + <paramref name="path"/>.</summary>
    public string Url(string path) => $"http://127.0.0.1:{Port}{path}";

    /// <summary><c>http://localhost:PORT</c> + <paramref name="path"/>: the same server, another site.</summary>
    public string LocalhostUrl(string path) => $"http://localhost:{Port}{path}";

    /// <summary>Answers <paramref name="path"/> (without its query) with <paramref name="reply"/>.</summary>
    public LoopbackCookieServer Map(string path, Reply reply) => Map(path, _ => reply);

    public LoopbackCookieServer Map(string path, Func<Request, Reply> reply)
    {
        _routes[path] = reply;
        return this;
    }

    /// <summary>Every request received for <paramref name="path"/> (without its query), in arrival order.</summary>
    public Request[] RequestsFor(string path) =>
        [.. _requests.Where(r => string.Equals(StripQuery(r.Path), path, StringComparison.Ordinal))];

    /// <summary>The one request received for <paramref name="path"/>; fails when there were none or several.</summary>
    public Request Single(string path)
    {
        var requests = RequestsFor(path);
        Assert.True(requests.Length == 1,
            $"expected one request for {path}, got {requests.Length}; received: " +
            string.Join(", ", _requests.Select(r => r.Path)));
        return requests[0];
    }

    private static string StripQuery(string path) => path.Split('?', 2)[0];

    private async Task AcceptAsync(TcpListener listener)
    {
        while (!_stopping.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(_stopping.Token);
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

                // Drain the request line, the headers and the body before answering: a response
                // written while the client is still sending can surface as a connection reset rather
                // than a response. The clients here always frame a body with Content-Length.
                var received = new List<byte>();
                var buffer = new byte[4096];
                int headerEnd;
                while ((headerEnd = HeaderEnd(received)) < 0)
                {
                    var read = await stream.ReadAsync(buffer, _stopping.Token);
                    if (read <= 0)
                        return;
                    received.AddRange(buffer.AsSpan(0, read).ToArray());
                }

                var lines = Encoding.Latin1.GetString(received.GetRange(0, headerEnd).ToArray()).Split("\r\n");
                var parts = lines[0].Split(' ');
                if (parts.Length < 2)
                    return;

                var fields = new List<(string Name, string Value)>();
                foreach (var line in lines.Skip(1))
                {
                    var colon = line.IndexOf(':');
                    if (colon > 0)
                        fields.Add((line[..colon].Trim(), line[(colon + 1)..].Trim()));
                }

                string? First(string name) =>
                    fields.Where(field => string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))
                        .Select(field => field.Value)
                        .FirstOrDefault();

                var length = int.TryParse(First("Content-Length"), out var declared) ? declared : 0;
                while (received.Count - headerEnd < length)
                {
                    var read = await stream.ReadAsync(buffer, _stopping.Token);
                    if (read <= 0)
                        break;
                    received.AddRange(buffer.AsSpan(0, read).ToArray());
                }

                var body = Encoding.UTF8.GetString(received.Skip(headerEnd).Take(length).ToArray());
                var request = new Request(
                    parts[0],
                    parts[1],
                    First("Host") ?? string.Empty,
                    First("Cookie"),
                    First("Origin"),
                    fields,
                    body);
                _requests.Enqueue(request);

                var reply = _routes.TryGetValue(StripQuery(request.Path), out var route)
                    ? route(request)
                    : new Reply(404, "text/plain", "not found");

                var payload = Encoding.UTF8.GetBytes(reply.Body);
                var head = new StringBuilder()
                    .Append($"HTTP/1.1 {reply.Status} {ReasonPhrase(reply.Status)}\r\n")
                    .Append($"Content-Type: {reply.ContentType}\r\n")
                    .Append($"Content-Length: {payload.Length}\r\n");
                foreach (var cookie in reply.SetCookies ?? [])
                    head.Append($"Set-Cookie: {cookie}\r\n");
                if (reply.Location is not null)
                    head.Append($"Location: {reply.Location}\r\n");
                foreach (var (name, value) in reply.Headers ?? [])
                    head.Append($"{name}: {value}\r\n");
                head.Append("Connection: close\r\n\r\n");

                await stream.WriteAsync(Encoding.Latin1.GetBytes(head.ToString()), _stopping.Token);
                await stream.WriteAsync(payload, _stopping.Token);
                await stream.FlushAsync(_stopping.Token);
            }
            catch (Exception) { /* the client hung up; nothing here is worth failing a test over */ }
        }
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        204 => "No Content",
        301 => "Moved Permanently",
        302 => "Found",
        303 => "See Other",
        307 => "Temporary Redirect",
        403 => "Forbidden",
        404 => "Not Found",
        _ => "Status",
    };

    /// <summary>The offset just past the blank line that ends the headers, or -1 before it arrives.</summary>
    private static int HeaderEnd(List<byte> received)
    {
        for (var i = 3; i < received.Count; i++)
        {
            if (received[i - 3] == '\r' && received[i - 2] == '\n' &&
                received[i - 1] == '\r' && received[i] == '\n')
                return i + 1;
        }

        return -1;
    }

    public void Dispose()
    {
        _stopping.Cancel();
        foreach (var listener in _listeners)
            listener.Stop();
        _stopping.Dispose();
    }
}
