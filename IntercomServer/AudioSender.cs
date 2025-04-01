using System.Net;
using System.Net.Sockets;

namespace IntercomServer;

internal class AudioSender : IDisposable
{
    private readonly UdpClient _client = new();
    private readonly Queue<(IPEndPoint EndPoint, byte[] Data)> _sendQueue = new();
    private readonly Lock _syncRoot = new();
    private int _nextPacketIndex;
    private bool _sendLoopRunning;

    public void Send(IPEndPoint endPoint, Span<byte> data)
    {
        const int maxDataSize =
            1472 /* max safe data size assuming an MTU of 1500 */
            - 4 /* packet index */
        ;

        for (int offset = 0; offset < data.Length; offset += maxDataSize)
        {
            int len = Math.Min(data.Length - offset, maxDataSize);

            var packetIndex = BitConverter.GetBytes(
                IPAddress.HostToNetworkOrder(_nextPacketIndex++)
            );
            var buffer = new byte[len + 4];

            Array.Copy(packetIndex, buffer, 4);
            data.Slice(offset, len).CopyTo(buffer.AsSpan(4, len));

            Enqueue(endPoint, buffer);
        }
    }

    private void Enqueue(IPEndPoint endPoint, byte[] data)
    {
        lock (_syncRoot)
        {
            _sendQueue.Enqueue((endPoint, data));

            if (!_sendLoopRunning)
            {
                _sendLoopRunning = true;

                BeginSend();
            }
        }
    }

    private void BeginSend()
    {
        var data = _sendQueue.Dequeue();

        _client.BeginSend(data.Data, data.Data.Length, data.EndPoint, EndSend, null);
    }

    private void EndSend(IAsyncResult ar)
    {
        _client.EndSend(ar);

        lock (_syncRoot)
        {
            if (_sendQueue.Count > 0)
                BeginSend();
            else
                _sendLoopRunning = false;
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
