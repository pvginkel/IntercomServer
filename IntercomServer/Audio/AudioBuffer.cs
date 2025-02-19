using System.Buffers;

namespace IntercomServer.Audio;

internal class AudioBuffer(AudioFormat format, TimeSpan leadBuffer, TimeSpan trailBuffer)
{
    private static byte[] AllocateBuffer(
        AudioFormat format,
        TimeSpan leadBuffer,
        TimeSpan trailBuffer
    )
    {
        var bufferSize = (int)(
            format.BytesPerSecond * (leadBuffer.TotalSeconds + trailBuffer.TotalSeconds)
        );

        return new byte[bufferSize];
    }

    private readonly int _leadBytes = (int)(format.BytesPerSecond * leadBuffer.TotalSeconds);
    private readonly int _trailBytes = (int)(format.BytesPerSecond * trailBuffer.TotalSeconds);
    private readonly byte[] _buffer = AllocateBuffer(format, leadBuffer, trailBuffer);
    private int _readOffset;
    private int _writeOffset;

    public int BufferUsed => ((_writeOffset + _buffer.Length) - _readOffset) % _buffer.Length;
    public int BufferFree => _buffer.Length - BufferUsed;

    public void Reset()
    {
        _readOffset = 0;
        _writeOffset = 0;
    }

    public void Append(ReadOnlySequence<byte> data)
    {
        if (data.Length > _buffer.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(data),
                $"Data does not fit in the buffer; data length '{data.Length}', buffer size '{_buffer.Length}'"
            );
        }

        // If we don't have data buffered, zero out the lead fragment of the buffer and
        // start writing after it.

        if (BufferUsed == 0)
        {
            _readOffset = 0;
            _writeOffset = _leadBytes;

            Array.Fill<byte>(_buffer, 0, 0, _writeOffset);
        }

        // Copy the sample into our buffer in one or two parts, depending on whether
        // the sample wraps the end of our buffer.

        var overflow = (int)data.Length - BufferFree;
        if (overflow > 0)
            ShiftReadOffset(overflow);

        var chunk1 = _buffer.Length - _writeOffset;
        var copy1 = Math.Min((int)data.Length, chunk1);

        data.Slice(0, copy1).CopyTo(_buffer.AsSpan().Slice(_writeOffset));
        ShiftWriteOffset(copy1);

        var copy2 = (int)data.Length - chunk1;
        if (copy2 > 0)
        {
            data.Slice(copy1).CopyTo(_buffer.AsSpan().Slice(_writeOffset));
            ShiftWriteOffset(copy2);
        }
    }

    public int Take(byte[] buffer)
    {
        var bufferUsed = BufferUsed;

        // Zero out the part of the buffer we're not writing.

        if (bufferUsed < buffer.Length)
            Array.Fill<byte>(buffer, 0, bufferUsed, buffer.Length - bufferUsed);

        // Copy to the buffer in one or two parts, depending on whether we're reading
        // beyond the end of the internal buffer.

        var copy = Math.Min(buffer.Length, bufferUsed);

        var chunk1 = _buffer.Length - _readOffset;
        var copy1 = Math.Min(chunk1, copy);

        _buffer.AsSpan().Slice(_readOffset, copy1).CopyTo(buffer);
        ShiftReadOffset(copy1);

        var copy2 = copy - copy1;
        if (copy2 > 0)
        {
            _buffer.AsSpan().Slice(_readOffset, copy2).CopyTo(buffer.AsSpan().Slice(copy1));
            ShiftReadOffset(copy2);
        }

        return copy;
    }

    private void ShiftReadOffset(int offset)
    {
        if (offset > BufferUsed)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                "Progress offset cannot extend beyond the number of bytes in use"
            );
        }

        _readOffset = (_readOffset + offset) % _buffer.Length;

        if (_readOffset == _writeOffset)
        {
            _readOffset = 0;
            _writeOffset = 0;
        }
    }

    private void ShiftWriteOffset(int offset)
    {
        if (offset > BufferFree)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                "Progress offset cannot extend beyond the number of free bytes"
            );
        }

        _writeOffset = (_writeOffset + offset) % _buffer.Length;
    }
}
