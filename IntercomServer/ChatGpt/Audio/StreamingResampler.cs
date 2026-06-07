namespace IntercomServer.ChatGpt.Audio;

/// <summary>
/// A small streaming linear resampler for mono 16-bit PCM. It keeps fractional
/// position and the trailing sample across calls, so consecutive buffers are
/// resampled continuously without clicks at the boundaries.
///
/// The intercom uses 16 kHz mono PCM16 while the OpenAI Realtime API uses
/// 24 kHz mono PCM16, so one instance is used per direction per conversation.
/// Not thread-safe: each instance is driven from a single thread.
/// </summary>
internal sealed class StreamingResampler(int inputRate, int outputRate)
{
    private readonly double _step = (double)inputRate / outputRate;

    // Position of the next output sample, expressed in input-sample units
    // relative to the start of the current input block. May be negative, in
    // which case it refers to the carried-over trailing sample (virtual index -1).
    private double _inPos;

    // Trailing sample of the previous block (the sample at virtual index -1).
    private short _prev;

    public byte[] Resample(ReadOnlySpan<byte> input)
    {
        if (inputRate == outputRate)
            return input.ToArray();

        int n = input.Length / 2;
        if (n == 0)
            return [];

        Span<short> samples = n <= 1024 ? stackalloc short[n] : new short[n];
        for (int i = 0; i < n; i++)
            samples[i] = (short)(input[i * 2] | (input[i * 2 + 1] << 8));

        var output = new List<short>((int)(n / _step) + 2);

        // Produce output samples while both interpolation neighbours are
        // available: floor(_inPos) in [-1, n-2] and floor(_inPos)+1 in [0, n-1].
        while (_inPos < n - 1)
        {
            int i = (int)Math.Floor(_inPos);
            double frac = _inPos - i;
            short s0 = i < 0 ? _prev : samples[i];
            short s1 = i + 1 < 0 ? _prev : samples[i + 1];
            int sample = (int)Math.Round(s0 + (s1 - s0) * frac);
            output.Add((short)Math.Clamp(sample, short.MinValue, short.MaxValue));
            _inPos += _step;
        }

        _prev = samples[n - 1];
        _inPos -= n;
        if (_inPos < -1)
            _inPos = -1;

        var bytes = new byte[output.Count * 2];
        for (int i = 0; i < output.Count; i++)
        {
            bytes[i * 2] = (byte)output[i];
            bytes[i * 2 + 1] = (byte)(output[i] >> 8);
        }
        return bytes;
    }
}
