using System.Net;
using System.Net.Sockets;

namespace Broiler.HtmlBridge.Tests;

/// <summary>
/// A loopback listener at an ephemeral port Broiler.Net will connect to.
/// </summary>
/// <remarks>
/// The OS hands out any free port, and on a machine whose dynamic range starts at 1024 that can be one of the
/// Fetch standard's bad ports -- 6665 to 6669 among them -- which Broiler.Net refuses, as a browser does. The
/// test that drew one failed with "Port 6667 is blocked", or waited for a frame that never loaded, and passed
/// when it ran again.
/// </remarks>
internal static class LoopbackPorts
{
    // Fetch §2.9 "bad port", from 1024 up: the ones an ephemeral port can be.
    private static readonly HashSet<int> BadPorts =
        [1719, 1720, 1723, 2049, 3659, 4045, 4190, 5060, 5061, 6000, 6566, 6665, 6666, 6667, 6668, 6669, 6679, 6697, 10080];

    /// <summary>Whether a request to <paramref name="port"/> is one Broiler.Net refuses.</summary>
    internal static bool IsBad(int port) => BadPorts.Contains(port);

    /// <summary>
    /// A started listener on <paramref name="address"/> at an ephemeral port that is not a bad one. A bad one the OS
    /// hands out is held until a good one is found, so it is not handed out again meanwhile.
    /// </summary>
    internal static TcpListener Start(IPAddress address)
    {
        var refused = new List<TcpListener>();
        try
        {
            for (var attempt = 0; attempt < 32; attempt++)
            {
                var listener = new TcpListener(address, 0);
                listener.Start();
                if (!IsBad(((IPEndPoint)listener.LocalEndpoint).Port))
                    return listener;

                refused.Add(listener);
            }

            throw new InvalidOperationException("No ephemeral loopback port outside Fetch's bad ports after 32 attempts.");
        }
        finally
        {
            foreach (var listener in refused)
                listener.Stop();
        }
    }
}
