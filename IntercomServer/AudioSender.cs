using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace IntercomServer;

/// <summary>
/// Sends the server's UDP audio from one socket. Each device's datagrams are numbered by a
/// sequence counter of its own, kept by device id: it starts at zero the first time the device
/// is sent to, and runs on across everything sent to it, from any stream, while the server runs.
/// </summary>
internal class AudioSender : IDisposable
{
    private readonly UdpClient _client = new();
    private readonly Queue<(IPEndPoint EndPoint, byte[] Data)> _sendQueue = new();
    private readonly Dictionary<string, int> _nextPacketIndex = new();
    private readonly Lock _syncRoot = new();
    private bool _sendLoopRunning;

    public void Send(string deviceId, IPEndPoint endPoint, Span<byte> data)
    {
        const int maxDataSize =
            1472 /* max safe data size assuming an MTU of 1500 */
            - 4 /* packet index */
        ;

        for (int offset = 0; offset < data.Length; offset += maxDataSize)
        {
            int len = Math.Min(data.Length - offset, maxDataSize);

            var buffer = new byte[len + 4];
            data.Slice(offset, len).CopyTo(buffer.AsSpan(4, len));

            Enqueue(deviceId, endPoint, buffer);
        }
    }

    private void Enqueue(string deviceId, IPEndPoint endPoint, byte[] data)
    {
        lock (_syncRoot)
        {
            // Numbered under the lock that queues it, so streams sending to one device at once
            // never share an index, and the device's datagrams leave in the order of their indices.
            var packetIndex = _nextPacketIndex.GetValueOrDefault(deviceId);
            _nextPacketIndex[deviceId] = packetIndex + 1;
            BinaryPrimitives.WriteInt32BigEndian(data, packetIndex);

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
