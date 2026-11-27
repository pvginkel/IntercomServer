using IntercomServer.Tests.Harness;
using IntercomServer.Utils.Audio;
using Microsoft.Extensions.DependencyInjection;

namespace IntercomServer.Tests;

/// <summary>
/// What a device's UDP listener receives: 640-byte payloads, one 20 ms chunk at mono 16 kHz
/// 16-bit each, every one behind a 4-byte big-endian packet index. A device that advertises
/// another rate in <c>audio_formats.out</c> is sent its sounds at that rate, in 20 ms chunks of
/// it, each split past 1468 bytes. Each device has a counter of its own, which numbers every
/// packet the server sends it, from every stream, and runs on from one sound to the next.
/// </summary>
public sealed class PacketTests
{
    // How long nothing arrives before a test takes a sound to have ended: fifty chunks' time.
    private static readonly TimeSpan SoundEnded = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task A_sound_goes_out_in_640_byte_payloads_behind_a_big_endian_counter_that_runs_on_into_the_next_sound()
    {
        await using var harness = await ServerHarness.StartAsync();
        var device = await harness.AddDeviceAsync("0x0a0001");
        await device.ComeOnlineAsync();

        await harness.RingDoorbellAsync();

        var packets = new List<AudioPacket> { await device.NextPacketAsync() };
        while (await device.NextPacketWithinAsync(SoundEnded) is { } packet)
            packets.Add(packet);

        Assert.True(
            packets.Sum(p => p.Payload.Length)
                >= BuiltInSounds.Bytes(BuiltInSounds.DoorbellLength - BuiltInSounds.Margin),
            $"The doorbell stopped after {packets.Count} packets, short of its end."
        );
        Assert.All(packets.SkipLast(1), p => Assert.Equal(4 + 640, p.Datagram.Length));
        var last = packets[^1].Payload.Length;
        Assert.InRange(last, 2, 640);
        Assert.Equal(0, last % 2);
        // Read big-endian, the indices count up by one from the server's first packet.
        Assert.Equal(Enumerable.Range(0, packets.Count), packets.Select(p => p.Index));

        await harness.RingDoorbellAsync();

        Assert.Equal(packets.Count, (await device.NextPacketAsync()).Index);
    }

    [Fact]
    public async Task Each_device_numbers_its_packets_with_a_counter_of_its_own()
    {
        await using var harness = await ServerHarness.StartAsync();
        var first = await harness.AddDeviceAsync("0x0a0001");
        var second = await harness.AddDeviceAsync("0x0a0002");
        await first.ComeOnlineAsync();
        await second.PublishConfigurationAsync();
        await second.PublishStateAsync(enabled: false);

        // Disabled, the second device is not reached by this doorbell.
        await harness.RingDoorbellAsync();
        var toFirst = await first.NextPacketsAsync(25);

        // This one reaches both, and replaces the first doorbell on the first device.
        await second.PublishStateAsync();
        await harness.RingDoorbellAsync();
        var toSecond = await second.NextPacketsAsync(25);
        toFirst.AddRange(first.TakeArrivedPackets());
        toFirst.AddRange(await first.NextPacketsAsync(25));

        // The second device's counter starts at zero, whatever went to the first, and each
        // device's packets are numbered one after another, across the two doorbells on the first.
        Assert.Equal(Enumerable.Range(0, 25), toSecond.Select(p => p.Index));
        Assert.Equal(Enumerable.Range(0, toFirst.Count), toFirst.Select(p => p.Index));
    }

    [Fact]
    public async Task A_live_stream_to_a_device_draws_from_the_counter_its_sounds_draw_from()
    {
        await using var harness = await ServerHarness.StartAsync();
        var chatting = await harness.AddDeviceAsync("0x0a0001");
        var control = await harness.AddDeviceAsync("0x0a0002");
        await chatting.ComeOnlineAsync();
        await control.ComeOnlineAsync();

        await harness.RingDoorbellAsync();
        var toChatting = await chatting.NextPacketsAsync(5);

        // A fifth of a second beside the doorbell, as a ChatGPT reply plays beside it.
        await using (var reply = LivePlayback.Start(harness, chatting))
        {
            reply.Append(10);
            await reply.CompleteAsync();
        }

        var longest = BuiltInSounds.DoorbellLength + BuiltInSounds.Margin;
        var toControl = await control.NextSoundAsync(longest);
        toChatting.AddRange(await chatting.NextSoundAsync(longest));

        // The chatting device got the doorbell and the reply, numbered as one run by its counter,
        // and the control got the doorbell alone, numbered by its own.
        Assert.Equal(toControl.Count + 10, toChatting.Count);
        Assert.Equal(Enumerable.Range(0, toChatting.Count), toChatting.Select(p => p.Index));
        Assert.Equal(Enumerable.Range(0, toControl.Count), toControl.Select(p => p.Index));
    }

    [Fact]
    public async Task Streams_sending_to_one_device_at_once_never_share_an_index()
    {
        const int streams = 8;
        const int datagrams = 200;

        await using var harness = await ServerHarness.StartAsync();
        var device = await harness.AddDeviceAsync("0x0a0001");
        var sender = harness.Services.GetRequiredService<AudioSender>();

        // A thread each, released at once, so their sends overlap.
        using var start = new Barrier(streams);
        await Task.WhenAll(
            Enumerable
                .Range(0, streams)
                .Select(_ =>
                    Task.Factory.StartNew(
                        () =>
                        {
                            start.SignalAndWait(TestContext.Current.CancellationToken);
                            for (var i = 0; i < datagrams; i++)
                                sender.Send(device.DeviceId, device.AudioEndpoint, new byte[2]);
                        },
                        TaskCreationOptions.LongRunning
                    )
                )
        );

        var indices = new List<int>();
        while (await device.NextPacketWithinAsync(SoundEnded) is { } packet)
            indices.Add(packet.Index);

        // Loopback may drop datagrams sent this fast, but it neither repeats nor reorders them:
        // each index is one of the counter's and later than the one before it.
        Assert.NotEmpty(indices);
        Assert.All(
            indices.Zip(indices.Skip(1)),
            pair => Assert.True(pair.First < pair.Second, $"Index {pair.Second} came after {pair.First}.")
        );
        Assert.InRange(indices[^1], 0, streams * datagrams - 1);
    }

    [Fact]
    public async Task Each_device_is_sent_its_sounds_at_the_rate_it_advertises_in_20_ms_chunks_of_it()
    {
        // A rate and its 20 ms chunk in bytes, mono 16-bit, from the lowest rate the server
        // produces to the highest: at 11025 Hz, 20 ms is 220.5 samples, and a chunk carries
        // whole ones.
        (int SampleRate, int ChunkBytes)[] formats =
        [
            (16000, 640),
            (8000, 320),
            (11025, 440),
            (48000, 1920),
            (192000, 7680),
        ];

        await using var harness = await ServerHarness.StartAsync();
        var devices = new List<TestDevice>();
        foreach (var (sampleRate, _) in formats)
        {
            var device = await harness.AddDeviceAsync($"0x0a{sampleRate:x4}");
            var configuration = device.DefaultConfiguration();
            configuration["audio_formats"]!["out"]!["sample_rate"] = sampleRate;
            await device.PublishConfigurationAsync(configuration);
            await device.PublishStateAsync();
            devices.Add(device);
        }

        await harness.RingDoorbellAsync();

        foreach (var (device, (sampleRate, chunkBytes)) in devices.Zip(formats))
        {
            var packets = new List<AudioPacket> { await device.NextPacketAsync() };
            while (await device.NextPacketWithinAsync(SoundEnded) is { } packet)
                packets.Add(packet);
            var sent = packets.SelectMany(p => p.Payload.ToArray()).ToArray();
            var format = new AudioFormat(AudioChannelLayout.Mono, sampleRate, 16);

            // The doorbell's length at the device's rate, and the server's decode of it in the
            // device's format, byte for byte.
            Assert.InRange(
                TimeSpan.FromSeconds((double)sent.Length / format.BytesPerSecond),
                BuiltInSounds.DoorbellLength - BuiltInSounds.Margin,
                BuiltInSounds.DoorbellLength + BuiltInSounds.Margin
            );
            var decoded = await BuiltInSounds.DecodedAsync(harness, Constants.AudioFiles.Doorbell, format);
            Assert.True(
                sent.AsSpan().SequenceEqual(decoded),
                $"The device at {sampleRate} Hz was sent {sent.Length} bytes, not the {decoded.Length} of the doorbell decoded at its rate."
            );
            Assert.Equal(PayloadSizes(sent.Length, chunkBytes), packets.Select(p => p.Payload.Length));
            Assert.Equal(Enumerable.Range(0, packets.Count), packets.Select(p => p.Index));
        }
    }

    // The payloads of `bytes` sent in chunks of `chunkBytes`, each chunk split into datagrams of
    // at most 1468 bytes, 1472 less the index.
    private static List<int> PayloadSizes(int bytes, int chunkBytes)
    {
        const int maxPayload = 1468;
        var sizes = new List<int>();
        for (var chunkStart = 0; chunkStart < bytes; chunkStart += chunkBytes)
        {
            var chunk = Math.Min(chunkBytes, bytes - chunkStart);
            for (var offset = 0; offset < chunk; offset += maxPayload)
                sizes.Add(Math.Min(maxPayload, chunk - offset));
        }
        return sizes;
    }
}
