using System.Net;
using System.Net.Sockets;

namespace IntercomServer.Tests.Harness;

/// <summary>
/// TCP ports for what a test starts that cannot bind port 0 and report the port it got: the
/// in-process broker. A port is free when it is picked, and another socket can take it before
/// the test binds it, so a start that finds its port taken picks another
/// (<see cref="StartAsync{T}"/>). A port picked and left unbound is one nothing listens on, for
/// a server pointed away from its broker.
/// </summary>
internal static class FreePort
{
    private const int Attempts = 5;

    /// <summary>
    /// Runs <paramref name="start"/> on a free port, and again on another one while the port
    /// it was given turns out to be taken.
    /// </summary>
    public static async Task<T> StartAsync<T>(Func<int, Task<T>> start)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await start(Pick());
            }
            catch (Exception ex) when (attempt < Attempts && IsAddressInUse(ex))
            {
            }
        }
    }

    public static int Pick()
    {
        var listener = TcpListener.Create(0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool IsAddressInUse(Exception ex) =>
        ex switch
        {
            SocketException socket => socket.SocketErrorCode == SocketError.AddressAlreadyInUse,
            _ => ex.InnerException != null && IsAddressInUse(ex.InnerException),
        };
}
