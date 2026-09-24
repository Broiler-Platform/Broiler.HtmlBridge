using System.Net;
using System.Net.Sockets;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// The fixture every cross-site case stands on: <c>localhost</c> must reach this server, not whatever
/// else holds <c>[::1]</c> at the port the OS handed out for 127.0.0.1.
/// </summary>
public class LoopbackCookieServerTests
{
    [Fact]
    public void APortWhoseIpv6LoopbackIsTakenIsNotUsed()
    {
        if (!Socket.OSSupportsIPv6)
            return;

        // Someone else holds [::1] at a port that is free on 127.0.0.1.
        var foreign = new TcpListener(IPAddress.IPv6Loopback, 0);
        foreign.Start();
        try
        {
            var port = ((IPEndPoint)foreign.LocalEndpoint).Port;
            var listeners = new List<TcpListener>();

            bool bound;
            try
            {
                bound = LoopbackCookieServer.TryBindLoopbackPair(port, listeners, out _);
            }
            catch (SocketException)
            {
                // 127.0.0.1 at that port is taken as well; nothing to show on this machine.
                return;
            }

            Assert.False(bound);
            Assert.Empty(listeners);
        }
        finally
        {
            foreign.Stop();
        }
    }

    [Fact]
    public async Task BothSitesReachTheServer()
    {
        using var server = new LoopbackCookieServer()
            .Map("/ping", new LoopbackCookieServer.Reply(ContentType: "text/plain", Body: "pong"));
        using var client = new HttpClient();

        Assert.Equal("pong", await client.GetStringAsync(server.Url("/ping")));
        Assert.Equal("pong", await client.GetStringAsync(server.LocalhostUrl("/ping")));
    }
}
