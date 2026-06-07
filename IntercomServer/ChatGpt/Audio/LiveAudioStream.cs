using System.Threading.Channels;

namespace IntercomServer.ChatGpt.Audio;

/// <summary>
/// A read-only <see cref="Stream"/> fed live with PCM audio. <see cref="Append"/> never
/// blocks; reads block until data is available and return 0 once <see cref="Complete"/> has
/// been called and the buffer is drained. This adapts ChatGPT's incrementally-arriving
/// audio to the paced sender (<see cref="AudioStreaming"/>). <see cref="Discard"/> drops
/// everything buffered, for barge-in.
/// </summary>
internal sealed class LiveAudioStream : Stream
{
    private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = false, SingleWriter = true }
    );
    private readonly Lock _syncRoot = new();
    private byte[]? _current;
    private int _offset;

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.Length > 0)
            _channel.Writer.TryWrite(data.ToArray());
    }

    public void Complete() => _channel.Writer.TryComplete();

    /// <summary>Drops all buffered audio (e.g. when the user interrupts the assistant).</summary>
    public void Discard()
    {
        lock (_syncRoot)
        {
            _current = null;
            _offset = 0;
        }

        while (_channel.Reader.TryRead(out _)) { }
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        while (true)
        {
            lock (_syncRoot)
            {
                if (_current != null && _offset < _current.Length)
                {
                    var toCopy = Math.Min(buffer.Length, _current.Length - _offset);
                    _current.AsSpan(_offset, toCopy).CopyTo(buffer.Span);
                    _offset += toCopy;
                    return toCopy;
                }

                _current = null;
                _offset = 0;
            }

            try
            {
                if (!await _channel.Reader.WaitToReadAsync(cancellationToken))
                    return 0;
            }
            catch (OperationCanceledException)
            {
                return 0;
            }

            if (_channel.Reader.TryRead(out var next))
            {
                lock (_syncRoot)
                {
                    _current = next;
                    _offset = 0;
                }
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();
}
