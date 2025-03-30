using System.Net;

namespace IntercomTest;

internal class IntercomUDPDataEventArgs(IPEndPoint remoteEndpoint, byte[] data) : EventArgs
{
    public IPEndPoint RemoteEndpoint { get; } = remoteEndpoint;

    public byte[] Data { get; } = data;
}
