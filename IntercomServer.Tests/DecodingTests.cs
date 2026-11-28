using System.Buffers.Binary;
using System.Diagnostics;
using IntercomServer.Tests.Harness;
using IntercomServer.Utils.Audio;
using Microsoft.Extensions.DependencyInjection;

namespace IntercomServer.Tests;

/// <summary>
/// FFmpeg decodes the built-in sounds once, when the server starts, into mono 16 kHz 16-bit,
/// and once more into each other format a device is sent them in, and a device is sent exactly
/// what it decoded. A server that cannot decode does not start. An ambient track's decode also
/// measures, with FFmpeg's EBU R128 meter, the audio it decoded into the device's format. A
/// decode whose read fails, or that is abandoned, ends, and its FFmpeg with it.
/// These run the FFmpeg on the PATH, whose version is not the one production runs, so they read
/// a sound's length and level, never bytes one FFmpeg makes and another may not.
/// </summary>
public sealed class DecodingTests
{
    [Theory]
    [InlineData(Constants.AudioFiles.Doorbell)]
    [InlineData(Constants.AudioFiles.Ring)]
    [InlineData(Constants.AudioFiles.Rejected)]
    public async Task The_server_decodes_each_built_in_sound_at_its_start_into_mono_16_kHz_16_bit(
        string fileName
    )
    {
        await using var harness = await ServerHarness.StartAsync();

        var pcm = await BuiltInSounds.DecodedAsync(harness, fileName, Constants.AudioFormat);

        // Decoded at another rate, channel count or sample width, the sound would be twice its
        // length or more, or half of it.
        var length = TimeSpan.FromSeconds((double)pcm.Length / BuiltInSounds.BytesPerSecond);
        var expected = fileName switch
        {
            Constants.AudioFiles.Doorbell => BuiltInSounds.DoorbellLength,
            Constants.AudioFiles.Ring => BuiltInSounds.RingLength,
            _ => BuiltInSounds.RejectedLength,
        };
        Assert.InRange(length, expected - BuiltInSounds.Margin, expected + BuiltInSounds.Margin);
        Assert.Equal(0, pcm.Length % 2);

        // Decoded, each sound peaks above -12 dBFS; silence, or a decode that lost the signal,
        // stays far below a tenth of full scale.
        var peak = 0;
        for (var i = 0; i < pcm.Length; i += 2)
            peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i))));
        Assert.True(peak > short.MaxValue / 10, $"{fileName} decoded to near silence: its peak is {peak}.");
    }

    // The rates a device may advertise run from 8 to 192 kHz.
    [Theory]
    [InlineData(8000)]
    [InlineData(11025)]
    [InlineData(16000)]
    [InlineData(48000)]
    [InlineData(192000)]
    public async Task The_server_decodes_into_signed_16_bit_little_endian_samples_at_the_rate_it_is_asked_for(
        int sampleRate
    )
    {
        await using var harness = await ServerHarness.StartAsync();
        var format = new AudioFormat(AudioChannelLayout.Mono, sampleRate, 16);
        short[] samples =
        [
            .. Enumerable
                .Range(0, format.SampleRate / 2)
                .Select(i => (short)(16000 * Math.Sin(2 * Math.PI * 440 * i / format.SampleRate))),
            short.MinValue,
            -256,
            -1,
            0,
            1,
            255,
            256,
            short.MaxValue,
        ];

        // A WAV already in the format decoded into passes through every FFmpeg unchanged, so the
        // decode is the samples themselves in the encoding the server sends. Decoded at another
        // rate, they would be resampled.
        var pcm = await harness
            .Services.GetRequiredService<AudioDecoder>()
            .DecodeAsync(
                "a WAV",
                new MemoryStream(Tracks.Wav(samples, format)),
                format,
                TestContext.Current.CancellationToken
            );

        var expected = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(expected.AsSpan(i * 2), samples[i]);
        Assert.Equal(expected, pcm);
    }

    [Fact]
    public async Task A_device_is_sent_the_doorbell_the_server_decoded_byte_for_byte()
    {
        await using var harness = await ServerHarness.StartAsync();
        var device = await harness.AddDeviceAsync("0x0a0001");
        await device.ComeOnlineAsync();

        await harness.RingDoorbellAsync();

        var packets = await device.NextSoundAsync(BuiltInSounds.DoorbellLength + BuiltInSounds.Margin);
        var sent = packets.OrderBy(packet => packet.Index).SelectMany(packet => packet.Payload.ToArray()).ToArray();
        var decoded = await BuiltInSounds.DecodedAsync(
            harness,
            Constants.AudioFiles.Doorbell,
            Constants.AudioFormat
        );
        Assert.True(
            sent.AsSpan().SequenceEqual(decoded),
            $"The device was sent {sent.Length} bytes in {packets.Count} packets, which are not the {decoded.Length} bytes decoded."
        );
    }

    [Fact]
    public async Task A_decode_that_fails_is_not_kept_and_the_next_playback_decodes_again()
    {
        await using var harness = await ServerHarness.StartAsync();
        // A rate FFmpeg refuses, which no device can make the server decode at.
        var refused = new AudioFormat(AudioChannelLayout.Mono, -1, 16);

        var first = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BuiltInSounds.DecodedAsync(harness, Constants.AudioFiles.Doorbell, refused)
        );
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BuiltInSounds.DecodedAsync(harness, Constants.AudioFiles.Doorbell, refused)
        );

        // A decode that was kept would fail the second playback with the first one's error.
        Assert.StartsWith("FFmpeg could not decode doorbell.mp3: it exited with code ", second.Message);
        Assert.NotSame(first, second);
    }

    [Fact]
    public async Task A_server_without_FFmpeg_stops_at_its_start_saying_FFmpeg_is_missing()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"no-ffmpeg-{Guid.NewGuid():N}", "ffmpeg");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ServerHarness.StartAsync(services: services => services.AddSingleton(new AudioDecoder(missing)))
        );

        Assert.StartsWith("FFmpeg is missing: ", error.Message);
        Assert.Contains($"'{missing}' could not be started", error.Message);
    }

    // A few bytes, and more than FFmpeg reads before it gives up, which leaves the server
    // writing to an FFmpeg that has already exited.
    [Theory]
    [InlineData(17)]
    [InlineData(8_000_000)]
    public async Task What_FFmpeg_cannot_decode_fails_with_its_exit_naming_what_was_decoded(int bytes)
    {
        var decoder = new AudioDecoder("ffmpeg");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            decoder.DecodeAsync(
                "not-audio",
                new MemoryStream(new byte[bytes]),
                Constants.AudioFormat,
                TestContext.Current.CancellationToken
            )
        );

        Assert.StartsWith("FFmpeg could not decode not-audio: it exited with code ", error.Message);
    }

    // A recording's tone is at an eighth of full scale: a peak of -18.1 dBFS, and the loudness
    // of its power, -0.691 + 10 log10((1/8)^2 / 2) LUFS, since K-weighting is all but flat at
    // 440 Hz. A lossy codec moves either by a few tenths.
    private const double ToneLoudness = -21.8;

    private static readonly TimeSpan RecordingLength = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData("mp3", 16000)]
    [InlineData("m4a", 16000)]
    [InlineData("wav", 16000)]
    [InlineData("mp3", 48000)]
    [InlineData("m4a", 48000)]
    [InlineData("wav", 48000)]
    public async Task A_mono_recording_measures_at_its_tones_loudness_and_its_peak_is_the_peak_of_the_audio_decoded(
        string extension,
        int sampleRate
    )
    {
        var format = new AudioFormat(AudioChannelLayout.Mono, sampleRate, 16);

        var measured = await new AudioDecoder("ffmpeg").DecodeMeasuredAsync(
            "a recording",
            new MemoryStream(await Tracks.RecordingAsync(extension, RecordingLength)),
            format,
            TestContext.Current.CancellationToken
        );

        AssertLength(RecordingLength, measured.Pcm, format);
        AssertMeasures(measured);
        Assert.InRange(measured.Loudness, ToneLoudness - 0.5, ToneLoudness + 0.5);
    }

    // A meter that read the recording's own channels would read the tone's loudness, as it does
    // in a mono recording; the device is sent their mix.
    [Theory]
    [InlineData("m4a", 16000)]
    [InlineData("wav", 16000)]
    [InlineData("m4a", 48000)]
    [InlineData("wav", 48000)]
    public async Task A_stereo_recording_measures_as_the_mono_mix_the_device_is_sent(string extension, int sampleRate)
    {
        var format = new AudioFormat(AudioChannelLayout.Mono, sampleRate, 16);

        var measured = await new AudioDecoder("ffmpeg").DecodeMeasuredAsync(
            "a recording",
            new MemoryStream(await Tracks.LeftChannelRecordingAsync(extension, RecordingLength)),
            format,
            TestContext.Current.CancellationToken
        );

        AssertLength(RecordingLength, measured.Pcm, format);
        AssertMeasures(measured);
        // The mix halves a channel the other does not carry: 6 dB down.
        Assert.InRange(measured.Loudness, ToneLoudness - 6.5, ToneLoudness - 5.5);
    }

    [Fact]
    public async Task Silence_measures_at_the_meters_floor_with_a_peak_of_minus_infinity()
    {
        var format = new AudioFormat(AudioChannelLayout.Mono, 48000, 16);

        var measured = await new AudioDecoder("ffmpeg").DecodeMeasuredAsync(
            "silence",
            new MemoryStream(await Tracks.SilenceAsync("mp3", RecordingLength)),
            format,
            TestContext.Current.CancellationToken
        );

        AssertLength(RecordingLength, measured.Pcm, format);
        Assert.True(measured.Pcm.AsSpan().IndexOfAnyExcept((byte)0) < 0, "Silence decoded to sound.");
        Assert.Equal(-70.0, measured.Loudness);
        Assert.Equal(double.NegativeInfinity, measured.Peak);
    }

    // FFmpeg writes an M4A's index after its audio, as phones do, so a decode reads it by seeking
    // back from the index to the audio: further than a pipe holds once a recording runs to minutes.
    [Theory]
    [InlineData(nameof(AudioDecoder.DecodeAsync))]
    [InlineData(nameof(AudioDecoder.DecodeMeasuredAsync))]
    public async Task An_M4A_of_minutes_with_its_index_after_its_audio_decodes_whole(string decode)
    {
        var length = TimeSpan.FromMinutes(3);
        var recording = await Tracks.RecordingAsync("m4a", length);
        Assert.True(
            recording.AsSpan().IndexOf("moov"u8) > recording.AsSpan().IndexOf("mdat"u8),
            "FFmpeg wrote the M4A's index ahead of its audio."
        );

        var pcm = await DecodeAsync(
            new AudioDecoder("ffmpeg"),
            decode,
            new MemoryStream(recording),
            TestContext.Current.CancellationToken
        );

        AssertLength(length, pcm, Constants.AudioFormat);
    }

    [Theory]
    [InlineData(nameof(AudioDecoder.DecodeAsync))]
    [InlineData(nameof(AudioDecoder.DecodeMeasuredAsync))]
    public async Task A_read_that_fails_ends_the_decode_with_that_reads_error_and_its_FFmpeg_with_it(string decode)
    {
        using var ffmpeg = new NamedFfmpeg();
        // Linux fails a read of /proc/self/mem at offset 0, an address no process maps, with EIO.
        await using var input = new FileStream("/proc/self/mem", FileMode.Open, FileAccess.Read);
        // A decode that waited on FFmpeg's exit would wait on an input FFmpeg never sees the end of.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(Timeouts.Observe);

        var error = await Assert.ThrowsAsync<IOException>(() =>
            DecodeAsync(ffmpeg.Decoder, decode, input, timeout.Token)
        );

        Assert.Contains("Input/output error", error.Message);
        Assert.Empty(ffmpeg.Processes());
    }

    [Theory]
    [InlineData(nameof(AudioDecoder.DecodeAsync))]
    [InlineData(nameof(AudioDecoder.DecodeMeasuredAsync))]
    public async Task An_abandoned_decode_ends_its_FFmpeg(string decode)
    {
        using var ffmpeg = new NamedFfmpeg();
        var recording = await Tracks.RecordingAsync("mp3", RecordingLength);
        // FFmpeg has the start of the recording, and waits for the rest.
        var input = new HeldStream(recording, recording.Length / 2);
        using var abandon = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var decoding = DecodeAsync(ffmpeg.Decoder, decode, input, abandon.Token);
        await input.Held.WaitAsync(Timeouts.Observe, TestContext.Current.CancellationToken);
        Assert.Single(ffmpeg.Processes());
        await abandon.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => decoding);
        Assert.Empty(ffmpeg.Processes());
    }

    private static async Task<byte[]> DecodeAsync(
        AudioDecoder decoder,
        string decode,
        Stream input,
        CancellationToken cancellationToken
    ) =>
        decode switch
        {
            nameof(AudioDecoder.DecodeAsync) => await decoder.DecodeAsync(
                "a recording",
                input,
                Constants.AudioFormat,
                cancellationToken
            ),
            nameof(AudioDecoder.DecodeMeasuredAsync) => (
                await decoder.DecodeMeasuredAsync("a recording", input, Constants.AudioFormat, cancellationToken)
            ).Pcm,
            _ => throw new ArgumentOutOfRangeException(nameof(decode), decode, null),
        };

    private static void AssertLength(TimeSpan expected, byte[] pcm, AudioFormat format)
    {
        var length = TimeSpan.FromSeconds((double)pcm.Length / format.BytesPerSecond);
        Assert.InRange(length, expected - BuiltInSounds.Margin, expected + BuiltInSounds.Margin);
        Assert.Equal(0, pcm.Length % 2);
    }

    /// <summary>
    /// Asserts that the meter measured the PCM it returned: the peak of its samples, and, for a
    /// tone, the loudness of their power.
    /// </summary>
    private static void AssertMeasures(MeasuredAudio measured)
    {
        double power = 0;
        var peak = 0;
        for (var i = 0; i < measured.Pcm.Length; i += 2)
        {
            int sample = BinaryPrimitives.ReadInt16LittleEndian(measured.Pcm.AsSpan(i));
            power += (double)sample * sample;
            peak = Math.Max(peak, Math.Abs(sample));
        }
        power /= measured.Pcm.Length / 2 * 32768.0 * 32768.0;
        var peakLevel = 20 * Math.Log10(peak / 32768.0);
        var loudness = -0.691 + 10 * Math.Log10(power);

        // FFmpeg prints a level to a tenth.
        Assert.InRange(measured.Peak, peakLevel - 0.06, peakLevel + 0.06);
        Assert.InRange(measured.Loudness, loudness - 0.5, loudness + 0.5);
    }

    /// <summary>
    /// The FFmpeg on the PATH under a name of its own, which Linux names its processes by, so a
    /// test finds the FFmpeg its decoder started among those other tests run meanwhile.
    /// </summary>
    private sealed class NamedFfmpeg : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            $"intercom-server-tests-{Guid.NewGuid():N}"
        );

        // Linux keeps the first 15 characters of a process's name.
        private readonly string _name = $"ffmpeg-{Guid.NewGuid():N}"[..15];

        public NamedFfmpeg()
        {
            var onPath = Environment
                .GetEnvironmentVariable("PATH")!
                .Split(Path.PathSeparator)
                .Select(directory => Path.Combine(directory, "ffmpeg"))
                .First(File.Exists);
            Directory.CreateDirectory(_directory);
            var path = Path.Combine(_directory, _name);
            File.CreateSymbolicLink(path, onPath);
            Decoder = new AudioDecoder(path);
        }

        public AudioDecoder Decoder { get; }

        /// <summary>The ids of the processes that run this FFmpeg.</summary>
        public int[] Processes()
        {
            var processes = Process.GetProcessesByName(_name);
            var ids = processes.Select(process => process.Id).ToArray();
            foreach (var process in processes)
                process.Dispose();
            return ids;
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
