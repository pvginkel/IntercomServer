using System.Buffers.Binary;
using System.Diagnostics;
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
/// stream's mean packet size. It reckons, by device id too, how much of what it sent each device
/// still holds, the device playing in real time what it holds from its arrival, and which of the
/// server's sounds play to each, which pauses the device's ambient sound: a write
/// <see cref="SendUnlessInterrupted"/> makes goes out only while none does.
/// </summary>
internal class AudioSender : IDisposable
{
    private readonly UdpClient _client = new();
    private readonly Queue<(IPEndPoint EndPoint, byte[] Data)> _sendQueue = new();
    private readonly Dictionary<string, int> _nextPacketIndex = new();
    // Under the lock: the Stopwatch timestamp at which each device has played all it was sent.
    private readonly Dictionary<string, long> _playedUntil = new();
    // Under the lock: by device id, the sounds Interrupt marked that play to it, and the task the
    // last of them to end completes.
    private readonly Dictionary<string, (int Sounds, TaskCompletionSource Ended)> _interruptions = new();
    private readonly Lock _syncRoot = new();
    private bool _sendLoopRunning;

    public void Send(string deviceId, IPEndPoint endPoint, AudioFormat format, Span<byte> data) =>
        Send(deviceId, endPoint, format, data, unlessInterrupted: false);

    /// <summary>
    /// Sends as <see cref="Send(string, IPEndPoint, AudioFormat, Span{byte})"/> does, unless a sound
    /// <see cref="Interrupt"/> marked plays to the device: then it sends nothing and returns false.
    /// </summary>
    public bool SendUnlessInterrupted(string deviceId, IPEndPoint endPoint, AudioFormat format, Span<byte> data) =>
        Send(deviceId, endPoint, format, data, unlessInterrupted: true);

    /// <summary>
    /// Marks a sound as playing to the device <paramref name="deviceId"/> until the result is
    /// disposed, once: the device's ambient sound pauses while any such sound plays.
    /// </summary>
    public IDisposable Interrupt(string deviceId)
    {
        lock (_syncRoot)
        {
            _interruptions[deviceId] = _interruptions.TryGetValue(deviceId, out var interruption)
                ? interruption with { Sounds = interruption.Sounds + 1 }
                : (1, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        }

        return new Interruption(this, deviceId);
    }

    /// <summary>
    /// A task that completes once no sound <see cref="Interrupt"/> marked plays to the device
    /// <paramref name="deviceId"/>: completed already while none does.
    /// </summary>
    public Task Uninterrupted(string deviceId)
    {
        lock (_syncRoot)
            return _interruptions.TryGetValue(deviceId, out var interruption) ? interruption.Ended.Task : Task.CompletedTask;
    }

    /// <summary>How much audio the device <paramref name="deviceId"/> still holds of what it was sent.</summary>
    public TimeSpan Held(string deviceId)
    {
        lock (_syncRoot)
        {
            var now = Stopwatch.GetTimestamp();
            var playedUntil = _playedUntil.GetValueOrDefault(deviceId);
            return playedUntil > now ? Stopwatch.GetElapsedTime(now, playedUntil) : TimeSpan.Zero;
        }
    }

    private bool Send(string deviceId, IPEndPoint endPoint, AudioFormat format, Span<byte> data, bool unlessInterrupted)
    {
        const int maxDataSize =
            1472 /* max safe data size assuming an MTU of 1500 */
            - 4 /* packet index */
        ;

        var frameBytes = format.ChannelCount * (format.BitRate / 8);
        var maxPacketBytes = maxDataSize / frameBytes * frameBytes;
        var packets = (data.Length + maxPacketBytes - 1) / maxPacketBytes;
        var frames = data.Length / frameBytes;

        var buffers = new byte[packets][];
        var offset = 0;
        for (var packet = 0; packet < packets; packet++)
        {
            // The first `frames % packets` packets carry a frame more than the others, and the
            // last also carries the bytes of a partial frame the write ends on.
            var len =
                packet == packets - 1
                    ? data.Length - offset
                    : (frames / packets + (packet < frames % packets ? 1 : 0)) * frameBytes;

            buffers[packet] = new byte[len + 4];
            data.Slice(offset, len).CopyTo(buffers[packet].AsSpan(4, len));
            offset += len;
        }

        // One write's datagrams are numbered and queued under one hold of the lock, the check of
        // the device's interruptions with them, so none of them goes out after an Interrupt that
        // refuses the write.
        lock (_syncRoot)
        {
            if (unlessInterrupted && _interruptions.ContainsKey(deviceId))
                return false;

            var now = Stopwatch.GetTimestamp();
            _playedUntil[deviceId] =
                Math.Max(_playedUntil.GetValueOrDefault(deviceId), now) + frames * Stopwatch.Frequency / format.SampleRate;

            foreach (var buffer in buffers)
                Enqueue(deviceId, endPoint, buffer);
        }

        return true;
    }

    // Under the lock.
    private void Enqueue(string deviceId, IPEndPoint endPoint, byte[] data)
    {
        // Numbered under the lock that queues it, so streams sending to one device at once never
        // share an index, and the device's datagrams leave in the order of their indices.
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

    private void EndInterruption(string deviceId)
    {
        TaskCompletionSource? ended = null;
        lock (_syncRoot)
        {
            var interruption = _interruptions[deviceId];
            if (interruption.Sounds == 1)
            {
                _interruptions.Remove(deviceId);
                ended = interruption.Ended;
            }
            else
                _interruptions[deviceId] = interruption with { Sounds = interruption.Sounds - 1 };
        }

        ended?.SetResult();
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

    private sealed class Interruption(AudioSender sender, string deviceId) : IDisposable
    {
        public void Dispose() => sender.EndInterruption(deviceId);
    }
}
