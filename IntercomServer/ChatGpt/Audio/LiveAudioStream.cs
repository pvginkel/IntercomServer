namespace IntercomServer.ChatGpt.Audio;

/// <summary>
/// A read-only <see cref="Stream"/> fed live with PCM audio, with a small jitter buffer.
///
/// <see cref="Append"/> never blocks. Reads hold audio back until at least
/// <paramref name="prerollBytes"/> have accumulated (a "pre-roll") before releasing any,
/// which absorbs a bursty source so the device is not starved at the start of a talk
/// spurt. The pre-roll re-arms whenever the buffer drains — between turns, after
/// <see cref="Discard"/> (barge-in), or after an underrun. <see cref="Release"/> lets a
/// reply shorter than the pre-roll play out, and reads return 0 once <see cref="Complete"/>
/// has been called and the buffer is drained.
/// </summary>
internal sealed class LiveAudioStream(int prerollBytes) : Stream
{
    private readonly Lock _syncRoot = new();
    private readonly Queue<byte[]> _queue = new();
    private byte[]? _current;
    private int _offset;
    private long _queuedBytes;
    private bool _completed;
    private bool _buffering = true;
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
            return;

        lock (_syncRoot)
        {
            _queue.Enqueue(data.ToArray());
            _queuedBytes += data.Length;
            Wake();
        }
    }

    public void Complete()
    {
        lock (_syncRoot)
        {
            _completed = true;
            Wake();
        }
    }

    /// <summary>
    /// Stops pre-rolling so whatever is buffered plays even if it is shorter than the
    /// pre-roll (e.g. the model finished a short reply). The pre-roll re-arms once drained.
    /// </summary>
    public void Release()
    {
        lock (_syncRoot)
        {
            _buffering = false;
            Wake();
        }
    }

    /// <summary>Drops all buffered audio and re-arms the pre-roll (e.g. on barge-in).</summary>
    public void Discard()
    {
        lock (_syncRoot)
        {
            _queue.Clear();
            _queuedBytes = 0;
            _current = null;
            _offset = 0;
            _buffering = true;
        }
    }

    private void Wake()
    {
        // Must be called under _syncRoot. Continuations run asynchronously, so completing
        // the signal here does not re-enter the lock.
        var signal = _signal;
        _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.TrySetResult();
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default
    )
    {
        while (true)
        {
            Task signal;

            lock (_syncRoot)
            {
                // Serve the remainder of the current chunk first.
                if (_current != null && _offset < _current.Length)
                {
                    var toCopy = Math.Min(buffer.Length, _current.Length - _offset);
                    _current.AsSpan(_offset, toCopy).CopyTo(buffer.Span);
                    _offset += toCopy;
                    return toCopy;
                }

                _current = null;
                _offset = 0;

                if (_completed)
                {
                    if (_queue.Count > 0)
                    {
                        _current = _queue.Dequeue();
                        _queuedBytes -= _current.Length;
                        continue;
                    }

                    return 0;
                }

                // Once enough has accumulated, stop pre-rolling and start releasing audio.
                if (_buffering && _queuedBytes >= prerollBytes)
                    _buffering = false;

                if (!_buffering)
                {
                    if (_queue.Count > 0)
                    {
                        _current = _queue.Dequeue();
                        _queuedBytes -= _current.Length;
                        continue;
                    }

                    // The buffer drained (underrun) — re-arm the pre-roll.
                    _buffering = true;
                }

                signal = _signal.Task;
            }

            try
            {
                await signal.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return 0;
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
