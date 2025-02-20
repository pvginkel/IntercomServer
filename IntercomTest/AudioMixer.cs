using System.Buffers;
using IntercomServer.Utils.Audio;

namespace IntercomTest;

internal class AudioMixer(AudioFormat audioFormat, TimeSpan bufferInterval)
{
    private readonly byte[] _buffer = new byte[
        (int)(audioFormat.BytesPerSecond * bufferInterval.TotalSeconds * 2)
    ];
    private long _readOffset;
    private readonly Dictionary<string, long> _writeOffsets = new();

    public bool HasData => _writeOffsets.Count > 0;

    public void Reset()
    {
        _readOffset = 0;
        _writeOffsets.Clear();

        Array.Clear(_buffer);
    }

    public void Append(string topic, ReadOnlySequence<byte> buffer)
    {
        // If we don't have a write offset for this topic, we need to
        // start buffering.

        if (!_writeOffsets.TryGetValue(topic, out var writeOffset))
        {
            writeOffset = (int)(
                _readOffset + audioFormat.BytesPerSecond * bufferInterval.TotalSeconds
            );
        }

        var available = _buffer.Length - (writeOffset - _readOffset);
        var copy = (int)Math.Min(available, buffer.Length);
        var span = buffer.Slice(buffer.Length - copy, copy);

        var writeOffsetMod = (int)(writeOffset % _buffer.Length);

        var chunk1 = Math.Min(_buffer.Length - writeOffsetMod, copy);
        var span1 = _buffer.AsSpan(writeOffsetMod, chunk1);

        MixAudio(span.Slice(0, chunk1), span1);

        if (chunk1 < copy)
        {
            var chunk2 = copy - chunk1;
            var span2 = _buffer.AsSpan(0, chunk2);

            MixAudio(span.Slice(chunk1), span2);
        }

        _writeOffsets[topic] = writeOffset + copy;
    }

    private void MixAudio(ReadOnlySequence<byte> source, Span<byte> target)
    {
        AudioUtils.MixInBuffer(audioFormat, target, source.ToArray().AsSpan());
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
            if (_writeOffsets[topic] < _readOffset)
                _writeOffsets.Remove(topic);
        }
    }
}
