using System.Buffers.Binary;
using IntercomServer.Utils.Audio;

namespace IntercomServer.Tests.Harness;

/// <summary>
/// What a device receives of an ambient playback, read as samples, against the track the real
/// FFmpeg decodes: how a test sees the level, the fades and the loop's seam.
/// </summary>
internal static class AmbientAudio
{
    /// <summary>
    /// How long nothing arrives before <see cref="RestAsync"/> takes a playback to have ended:
    /// twenty-five chunks' time.
    /// </summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(0.5);

    /// <summary>The frames of the default fade, half a second, at <see cref="DeviceApi.ToiletFormat"/>'s rate.</summary>
    public const int Fade = 24000;

    /// <summary>The datagrams of <paramref name="length"/> of audio to a ToiletAutomation device: two each 20 ms.</summary>
    public static int Packets(TimeSpan length) => (int)(length.TotalSeconds * 100);

    /// <summary>The packets that arrive until none has for <see cref="Quiet"/>.</summary>
    public static async Task<List<AudioPacket>> RestAsync(TestDevice device)
    {
        var packets = new List<AudioPacket>();
        while (await device.NextPacketWithinAsync(Quiet) is { } packet)
            packets.Add(packet);
        return packets;
    }

    /// <summary>The samples of <paramref name="packets"/>, one after another.</summary>
    public static short[] Samples(IEnumerable<AudioPacket> packets)
    {
        var samples = new List<short>();
        foreach (var packet in packets)
        {
            for (var i = 0; i < packet.Payload.Length; i += 2)
                samples.Add(BinaryPrimitives.ReadInt16LittleEndian(packet.Payload[i..]));
        }
        return samples.ToArray();
    }

    /// <summary>The samples the real FFmpeg decodes <paramref name="file"/> into, in <paramref name="format"/>.</summary>
    public static async Task<short[]> DecodedAsync(byte[] file, AudioFormat format)
    {
        var pcm = await new AudioDecoder("ffmpeg").DecodeAsync(
            "a track",
            new MemoryStream(file),
            format,
            TestContext.Current.CancellationToken
        );
        var samples = new short[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2));
        return samples;
    }

    /// <summary>The loudness and the peak FFmpeg's EBU R128 meter measures in <paramref name="file"/>, decoded into <paramref name="format"/>.</summary>
    public static Task<MeasuredAudio> MeasuredAsync(byte[] file, AudioFormat format) =>
        new AudioDecoder("ffmpeg").DecodeMeasuredAsync(
            "a track",
            new MemoryStream(file),
            format,
            TestContext.Current.CancellationToken
        );

    /// <summary>Asserts <paramref name="packets"/> are numbered one after another, from <paramref name="first"/>.</summary>
    public static void AssertNumbered(IReadOnlyList<AudioPacket> packets, int first)
    {
        Assert.Equal(Enumerable.Range(first, packets.Count), packets.Select(packet => packet.Index));
    }

    /// <summary>
    /// Asserts <paramref name="received"/> is one playback of <paramref name="track"/>, from the
    /// track's start: the track looped without a gap at one gain, ramping up from silence over
    /// its first <paramref name="fade"/> frames, and, when <paramref name="fadedOut"/>, down to
    /// silence over its last. Each sample is within two of what that sends, the rounding of the
    /// gain and of the ramp. Returns the gain in dB.
    /// </summary>
    public static double AssertOnePlayback(short[] received, short[] track, int fade, bool fadedOut)
    {
        var end = fadedOut ? received.Length - fade : received.Length;
        Assert.True(end > fade, $"The playback sent {received.Length} samples, too few to hold its fades and a stretch between.");

        // The gain, by least squares over the stretch between the fades.
        double power = 0;
        double product = 0;
        for (var i = fade; i < end; i++)
        {
            double sample = track[i % track.Length];
            power += sample * sample;
            product += received[i] * sample;
        }
        var gain = product / power;

        for (var i = 0; i < received.Length; i++)
        {
            var level =
                i < fade ? (double)i / fade
                : i >= end ? (double)(received.Length - i) / fade
                : 1;
            var sent = gain * track[i % track.Length] * level;
            if (Math.Abs(received[i] - sent) > 2)
                Assert.Fail(
                    $"Sample {i} of {received.Length} is {received[i]}, where one playback of the track at a gain of {20 * Math.Log10(gain):0.00} dB sends {sent:0.0}."
                );
        }

        return 20 * Math.Log10(gain);
    }

    /// <summary>
    /// The index of the first of <paramref name="packets"/> that begins a playback of
    /// <paramref name="track"/>: the first whose samples correlate with the track's first, at
    /// whatever level the fade-in has them.
    /// </summary>
    public static int FirstPacketOf(IReadOnlyList<AudioPacket> packets, short[] track)
    {
        var index = packets.ToList().FindIndex(packet => Correlation(Samples([packet]), track) > 0.5);
        Assert.True(index >= 0, "No packet begins a playback of the track.");
        return index;
    }

    // The correlation of the samples with the track's first as many, independent of their levels:
    // about 0.87 for a linear ramp from silence, and a few hundredths for another track's noise.
    private static double Correlation(short[] samples, short[] track)
    {
        double product = 0;
        double received = 0;
        double sent = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            product += samples[i] * (double)track[i];
            received += samples[i] * (double)samples[i];
            sent += track[i] * (double)track[i];
        }
        return product / Math.Sqrt(received * sent);
    }

    /// <summary>The peak of <paramref name="samples"/>, in dBFS.</summary>
    public static double Peak(short[] samples) =>
        20 * Math.Log10(samples.Max(sample => Math.Abs((int)sample)) / 32768.0);
}
