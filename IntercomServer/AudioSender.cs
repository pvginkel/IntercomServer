using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using IntercomServer.Utils.Audio;

namespace IntercomServer;

/// <summary>
/// Sends the server's UDP audio from one socket. Each device's datagrams are numbered by a
/// sequence counter of its own, kept by device id: it starts at zero the first time the device
/// is sent to, and runs on across everything sent to it, from any stream, while the server runs.
/// Each write goes as the fewest datagrams of at most 1472 bytes that carry whole frames, their
/// payloads as equal as whole frames allow: the firmware fills a lost or late packet with the
/// stream's mean packet size.
/// </summary>
internal class AudioSender : IDisposable
{
    private readonly UdpClient _client = new();
    private readonly Queue<(IPEndPoint EndPoint, byte[] Data)> _sendQueue = new();
    private readonly Dictionary<string, int> _nextPacketIndex = new();
    private readonly Lock _syncRoot = new();
    private bool _sendLoopRunning;

    public void Send(string deviceId, IPEndPoint endPoint, AudioFormat format, Span<byte> data)
    {
        const int maxDataSize =
            1472 /* max safe data size assuming an MTU of 1500 */
            - 4 /* packet index */
        ;

        var frameBytes = format.ChannelCount * (format.BitRate / 8);
        var maxPacketBytes = maxDataSize / frameBytes * frameBytes;
        var packets = (data.Length + maxPacketBytes - 1) / maxPacketBytes;
        var frames = data.Length / frameBytes;

        var offset = 0;
        for (var packet = 0; packet < packets; packet++)
        {
            // The first `frames % packets` packets carry a frame more than the others, and the
            // last also carries the bytes of a partial frame the write ends on.
            var len =
                packet == packets - 1
                    ? data.Length - offset
                    : (frames / packets + (packet < frames % packets ? 1 : 0)) * frameBytes;

            var buffer = new byte[len + 4];
            data.Slice(offset, len).CopyTo(buffer.AsSpan(4, len));
            offset += len;

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
