using IntercomServer.Tests.Harness;

namespace IntercomServer.Tests;

/// <summary>
/// What a device's UDP listener receives: 640-byte payloads, one 20 ms chunk at mono 16 kHz
/// 16-bit each, every one behind a 4-byte big-endian packet index. One counter numbers every
/// packet the server sends, to every device and from every stream, and runs on from one sound
/// to the next. What a device advertises in <c>audio_formats</c> changes none of it.
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
    public async Task One_counter_numbers_the_packets_to_every_device()
    {
        await using var harness = await ServerHarness.StartAsync();
        var first = await harness.AddDeviceAsync("0x0a0001");
        var second = await harness.AddDeviceAsync("0x0a0002");
        await first.ComeOnlineAsync();
        await second.ComeOnlineAsync();

        await harness.RingDoorbellAsync();

        var toFirst = (await first.NextPacketsAsync(25)).Select(p => p.Index).ToList();
        var toSecond = (await second.NextPacketsAsync(25)).Select(p => p.Index).ToList();

        // Each chunk goes to one device and then the other, each send drawing the next index.
        Assert.All(toFirst.Zip(toFirst.Skip(1)), pair => Assert.Equal(pair.First + 2, pair.Second));
        Assert.All(toSecond.Zip(toSecond.Skip(1)), pair => Assert.Equal(pair.First + 2, pair.Second));
        Assert.Equal(Enumerable.Range(0, 50), toFirst.Concat(toSecond).Order());
    }

    [Fact]
    public async Task A_live_stream_through_the_servers_sender_draws_from_the_counter_a_sound_draws_from()
    {
        await using var harness = await ServerHarness.StartAsync();
        var ringing = await harness.AddDeviceAsync("0x0a0001");
        await ringing.ComeOnlineAsync();
        // It publishes nothing, so the doorbell does not reach it: it gets only the live stream.
        var chatting = await harness.AddDeviceAsync("0x0a0002");

        await harness.RingDoorbellAsync();
        var toRinging = (await ringing.NextPacketsAsync(5)).Select(p => p.Index).ToList();

        // A fifth of a second, well within the doorbell.
        await using (var reply = LivePlayback.Start(harness, chatting))
        {
            reply.Append(10);
            await reply.CompleteAsync();
        }
        var toChatting = (await chatting.NextPacketsAsync(10)).Select(p => p.Index).ToList();

        while (toRinging[^1] <= toChatting[^1])
            toRinging.Add((await ringing.NextPacketAsync()).Index);

        Assert.All(toChatting, index => Assert.InRange(index, toRinging[0] + 1, toRinging[^1] - 1));
        var ringingMeanwhile = toRinging.Count(index => index >= toChatting[0] && index <= toChatting[^1]);
        Assert.True(
            ringingMeanwhile < toChatting[^1] - toChatting[0] + 1,
            "The doorbell's indices did not skip the ones the live stream took."
        );
    }

    [Fact]
    public async Task What_a_device_advertises_changes_nothing_it_is_sent()
    {
        await using var harness = await ServerHarness.StartAsync();
        var standard = await harness.AddDeviceAsync("0x0a0001");
        await standard.ComeOnlineAsync();

        var unadvertised = await harness.AddDeviceAsync("0x0a0002");
        var configuration = unadvertised.DefaultConfiguration();
        configuration.Remove("audio_formats");
        await unadvertised.PublishConfigurationAsync(configuration);
        await unadvertised.PublishStateAsync();

        var otherRate = await harness.AddDeviceAsync("0x0a0003");
        configuration = otherRate.DefaultConfiguration();
        configuration["audio_formats"]!["out"]!["sample_rate"] = 48000;
        await otherRate.PublishConfigurationAsync(configuration);
        await otherRate.PublishStateAsync();

        await harness.RingDoorbellAsync();

        var expected = await Payloads(standard);
        // Two hex digits a byte: each payload is 640 bytes.
        Assert.All(expected, payload => Assert.Equal(640 * 2, payload.Length));
        Assert.Equal(expected, await Payloads(unadvertised));
        Assert.Equal(expected, await Payloads(otherRate));
    }

    // Half a second of what the device got, each payload as hex.
    private static async Task<List<string>> Payloads(TestDevice device) =>
        (await device.NextPacketsAsync(25)).Select(p => Convert.ToHexString(p.Payload)).ToList();
}
