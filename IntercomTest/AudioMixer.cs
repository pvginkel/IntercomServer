using System.Net;
using IntercomServer.Utils.Audio;
using Serilog;

namespace IntercomTest;

internal class AudioMixer(AudioFormat audioFormat, TimeSpan bufferInterval)
{
    private static readonly ILogger Logger = Log.ForContext<AudioMixer>();

    // The buffer is sized for the maximum delay so that only the per stream
    // playout delay changes when we adapt, never the allocation.

    private readonly byte[] _buffer = new byte[
        (int)(audioFormat.BytesPerSecond * StreamDelays.MaxDelay.TotalSeconds * 2)
    ];
    private long _readOffset;
    private readonly Dictionary<IPEndPoint, WriteOffset> _writeOffsets = new();
    private readonly object _syncRoot = new();

    public bool HasData
    {
        get
        {
            lock (_syncRoot)
            {
                return _writeOffsets.Count > 0;
            }
        }
    }

    public void Reset()
    {
        lock (_syncRoot)
        {
            _readOffset = 0;
            _writeOffsets.Clear();

            Array.Clear(_buffer);
        }
    }

    public void Append(IPEndPoint address, byte[] buffer)
    {
        lock (_syncRoot)
        {
            // If we don't have a write offset for this topic, we need to
            // start buffering.

            if (_writeOffsets.TryGetValue(address, out var writeOffset))
            {
                // Headroom is at its lowest just before new data arrives.
                // Sampling it here instead of in Take() keeps the drain at the
                // end of a stream out of the statistic.

                writeOffset.MinHeadroom = Math.Min(
                    writeOffset.MinHeadroom,
                    writeOffset.Offset - _readOffset
                );
            }
            else
            {
                var delayLength = ToBytes(StreamDelays.StartStream(address.Address, bufferInterval));

                writeOffset = new WriteOffset(
                    Offset: _readOffset + delayLength,
                    LastPacket: long.MinValue,
                    MinHeadroom: delayLength,
                    Started: StreamDelays.Uptime
                );
            }

            var packetIndex = IPAddress.NetworkToHostOrder(BitConverter.ToInt32(buffer, 0));
            if (packetIndex < writeOffset.LastPacket)
            {
                Logger.Warning(
                    "Dropping packet {PacketIndex} of remote {Remote}",
                    packetIndex,
                    address
                );
                return;
            }

            var data = buffer.AsSpan(4);

            var available = _buffer.Length - (writeOffset.Offset - _readOffset);
            var copy = (int)Math.Min(available, data.Length);
            var source = data.Slice(data.Length - copy, copy);

            var writeOffsetMod = (int)(writeOffset.Offset % _buffer.Length);

            var chunk1 = Math.Min(_buffer.Length - writeOffsetMod, copy);
            var target1 = _buffer.AsSpan(writeOffsetMod, chunk1);

            MixAudio(source[..chunk1], target1);

            if (chunk1 < copy)
            {
                var chunk2 = copy - chunk1;
                var target2 = _buffer.AsSpan(0, chunk2);

                MixAudio(source[chunk1..], target2);
            }

            _writeOffsets[address] = writeOffset with
            {
                Offset = writeOffset.Offset + copy,
                LastPacket = packetIndex
            };
        }
    }

    private void MixAudio(Span<byte> source, Span<byte> target)
    {
        AudioUtils.MixInBuffer(audioFormat, target, source);
    }

    public void Take(byte[] buffer)
    {
        if (buffer.Length > _buffer.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(buffer),
                "Buffer cannot exceed internal buffer size"
            );
        }

        lock (_syncRoot)
        {
            var readOffsetMod = (int)(_readOffset % _buffer.Length);
            var chunk1 = Math.Min(_buffer.Length - readOffsetMod, buffer.Length);
            var span1 = _buffer.AsSpan(readOffsetMod, chunk1);

            span1.CopyTo(buffer);
            span1.Clear();

            if (chunk1 < buffer.Length)
            {
                var chunk2 = buffer.Length - chunk1;
                var span2 = _buffer.AsSpan(0, chunk2);

                span2.CopyTo(buffer.AsSpan(chunk1));
                span2.Clear();
            }

            _readOffset += buffer.Length;

            // If any of the topics write offsets is less than what we've
            // read, it means we didn't have enough buffered. Start
            // buffering again. Record how the stream ended so the next
            // start of this stream can adapt the delay.

            foreach (var topic in _writeOffsets.Keys.ToList())
            {
                var writeOffset = _writeOffsets[topic];

                if (writeOffset.Offset < _readOffset)
                {
                    StreamDelays.EndStream(
                        topic.Address,
                        ToTimeSpan(writeOffset.MinHeadroom),
                        StreamDelays.Uptime - writeOffset.Started
                    );

                    _writeOffsets.Remove(topic);
                }
            }
        }
    }

    private long ToBytes(TimeSpan interval) =>
        (long)(audioFormat.BytesPerSecond * interval.TotalSeconds);

    private TimeSpan ToTimeSpan(long length) =>
        TimeSpan.FromSeconds(length / (double)audioFormat.BytesPerSecond);

    private record struct WriteOffset(
        long Offset,
        long LastPacket,
        long MinHeadroom,
        TimeSpan Started
    );
}
