using System.Net;
using IntercomServer.Utils.Audio;
using Serilog;

namespace IntercomTestWeb.Audio;

// Ported verbatim from IntercomTest's AudioMixer (it lived in the WPF project, not the shared Utils
// library). A fixed-size ring that buffers <paramref name="bufferInterval"/> of audio ahead of the
// read cursor and mixes multiple remote streams keyed by source endpoint, reordering/deduplicating by
// the 4-byte big-endian sequence index that prefixes every appended packet. Phase B runs one instance
// per direction at 100 ms: downlink (device -> browser) and uplink (browser -> device).
//
// Not internally synchronized — the WPF original was driven from one UI thread; the AudioBridge wraps
// each instance with its own lock because Append (receive thread) and Take (pump thread) now race.
internal sealed class AudioMixer(AudioFormat audioFormat, TimeSpan bufferInterval)
{
    private static readonly ILogger Logger = Log.ForContext<AudioMixer>();

    private readonly byte[] _buffer = new byte[
        (int)(audioFormat.BytesPerSecond * bufferInterval.TotalSeconds * 2)
    ];
    private long _readOffset;
    private readonly Dictionary<IPEndPoint, (long Offset, long LastPacket)> _writeOffsets = new();

    public bool HasData => _writeOffsets.Count > 0;

    public void Reset()
    {
        _readOffset = 0;
        _writeOffsets.Clear();

        Array.Clear(_buffer);
    }

    public void Append(IPEndPoint address, byte[] buffer)
    {
        // If we don't have a write offset for this topic, we need to
        // start buffering.

        if (!_writeOffsets.TryGetValue(address, out var writeOffset))
        {
            writeOffset = (
                Offset: (int)(
                    _readOffset + audioFormat.BytesPerSecond * bufferInterval.TotalSeconds
                ),
                LastPacket: long.MinValue
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

        _writeOffsets[address] = (Offset: writeOffset.Offset + copy, LastPacket: packetIndex);
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
        // buffering again.

        foreach (var topic in _writeOffsets.Keys.ToList())
        {
            if (_writeOffsets[topic].Offset < _readOffset)
                _writeOffsets.Remove(topic);
        }
    }
}
