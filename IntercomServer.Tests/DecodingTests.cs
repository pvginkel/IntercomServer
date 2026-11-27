using System.Buffers.Binary;
using IntercomServer.Tests.Harness;
using IntercomServer.Utils.Audio;
using Microsoft.Extensions.DependencyInjection;

namespace IntercomServer.Tests;

/// <summary>
/// FFmpeg decodes the built-in sounds once, when the server starts, into mono 16 kHz 16-bit,
/// and once more into each other format a device is sent them in, and a device is sent exactly
/// what it decoded. A server that cannot decode does not start.
/// These run the FFmpeg on the PATH, whose version is not the one production runs, so they read
/// a built-in sound's length and level, never bytes one FFmpeg makes and another may not.
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
                new MemoryStream(Wav(samples, format)),
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

    private static byte[] Wav(short[] samples, AudioFormat format)
    {
        var wav = new MemoryStream();
        using var writer = new BinaryWriter(wav);
        writer.Write("RIFF"u8);
        writer.Write(36 + samples.Length * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)format.ChannelCount);
        writer.Write(format.SampleRate);
        writer.Write(format.BytesPerSecond);
        writer.Write((short)(format.ChannelCount * 2));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples.Length * 2);
        foreach (var sample in samples)
            writer.Write(sample);
        writer.Flush();
        return wav.ToArray();
    }
}
