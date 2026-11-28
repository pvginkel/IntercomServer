using IntercomServer.Tests.Harness;

namespace IntercomServer.Tests;

/// <summary>
/// The server sends audio in real time, because the devices' jitter buffers are small. It keeps
/// a clock of how much audio it has sent: a chunk that goes out late is made up by sending the
/// next ones sooner, unless the source starved for longer than 60 ms, after which the clock
/// starts again from now. A built-in sound never starves, so the late chunk and the stall are
/// shown on the one source that does, the ChatGPT bridge's live stream (<see cref="LivePlayback"/>).
/// Each sits well clear of 60 ms, since a loaded machine stretches both. An ambient playback alone
/// runs ahead of real time: its prebuffer goes out over its first 100 ms, it then stays that far
/// ahead, and its cap, counted in real time from its start, fades it out. The class runs alone,
/// after the suite's other tests, so their servers do not stretch it too.
/// </summary>
[Collection(nameof(PacingTests))]
public sealed class PacingTests
{
    [Fact]
    public async Task A_sound_is_paced_to_real_time()
    {
        await using var harness = await ServerHarness.StartAsync();
        var device = await harness.AddDeviceAsync("0x0a0001");
        await device.ComeOnlineAsync();

        await harness.RingDoorbellAsync();

        // A second of the doorbell, well within its length.
        var packets = await device.NextPacketsAsync(50);

        var audio = TimeSpan.FromSeconds(
            (double)packets.SkipLast(1).Sum(p => p.Payload.Length) / BuiltInSounds.BytesPerSecond
        );
        Assert.InRange(
            packets[^1].ArrivedAfter(packets[0]),
            audio * 0.9,
            audio + TimeSpan.FromMilliseconds(250)
        );
    }

    [Fact]
    public async Task A_device_at_another_rate_is_paced_to_real_time_beside_a_16_kHz_device()
    {
        await using var harness = await ServerHarness.StartAsync();
        var standard = await harness.AddDeviceAsync("0x0a0001");
        var otherRate = await harness.AddDeviceAsync("0x0a0002");
        await standard.ComeOnlineAsync();
        var configuration = otherRate.DefaultConfiguration();
        configuration["audio_formats"]!["out"]!["sample_rate"] = 48000;
        await otherRate.PublishConfigurationAsync(configuration);
        await otherRate.PublishStateAsync();

        await harness.RingDoorbellAsync();

        // A second of the doorbell to each, well within its length: 50 chunks of 20 ms, a
        // datagram each at 16 kHz, and two at 48 kHz, where a chunk is 1920 bytes.
        var toStandard = await standard.NextPacketsAsync(50);
        var toOtherRate = await otherRate.NextPacketsAsync(100);

        // Each device's last chunk goes out 49 chunks' time after its first. Paced by 16 kHz's
        // clock, the device at 48 kHz would take three times that.
        var audio = 49 * LivePlayback.ChunkLength;
        Assert.InRange(
            toStandard[^1].ArrivedAfter(toStandard[0]),
            audio * 0.9,
            audio + TimeSpan.FromMilliseconds(250)
        );
        Assert.InRange(
            toOtherRate[^1].ArrivedAfter(toOtherRate[0]),
            audio * 0.9,
            audio + TimeSpan.FromMilliseconds(250)
        );
    }

    [Fact]
    public async Task A_chunk_that_comes_late_is_made_up()
    {
        const int rounds = 20;
        const int chunksPerRound = 5;

        await using var harness = await ServerHarness.StartAsync();
        var device = await harness.AddDeviceAsync("0x0a0001");
        await using var stream = LivePlayback.Start(harness, device);

        var packets = new List<AudioPacket>();
        stream.Append(chunksPerRound);
        for (var round = 1; ; round++)
        {
            packets.AddRange(await device.NextPacketsAsync(chunksPerRound));
            if (round == rounds)
                break;

            // The stream asks for the next chunk a chunk's time after the round's last went
            // out, and gets it about a chunk's time after that: late, but well short of a stall.
            await Task.Delay(2 * LivePlayback.ChunkLength, TestContext.Current.CancellationToken);
            stream.Append(chunksPerRound);
        }
        await stream.CompleteAsync();

        // A round's first chunk goes out late and the rest are made up, so a round's last goes
        // out on time, measured from the first round's last. Had each late round reset the
        // clock instead, each would end about 20 ms after the one before it, the stream 380 ms
        // late in all; half that is the tolerance either way.
        var onTime = packets[chunksPerRound - 1];
        var audio = (rounds - 1) * chunksPerRound * LivePlayback.ChunkLength;
        Assert.InRange(
            packets[^1].ArrivedAfter(onTime),
            audio - TimeSpan.FromMilliseconds(190),
            audio + TimeSpan.FromMilliseconds(190)
        );
    }

    [Fact]
    public async Task After_a_stall_the_clock_starts_again_instead_of_catching_up()
    {
        await using var harness = await ServerHarness.StartAsync();
        var device = await harness.AddDeviceAsync("0x0a0001");
        await using var stream = LivePlayback.Start(harness, device);

        stream.Append(10);
        await device.NextPacketsAsync(10);
        // The stream waits on its source for a second: a stall, far past 60 ms.
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        stream.Append(25);
        var afterStall = await device.NextPacketsAsync(25);
        await stream.CompleteAsync();

        // Paced from the stall's end, the 25 chunks take 480 ms. Caught up instead, a second
        // behind, they would all go out at once.
        var took = afterStall[^1].ArrivedAfter(afterStall[0]);
        Assert.True(
            took >= 12 * LivePlayback.ChunkLength,
            $"The 25 chunks after the stall went out in {took.TotalMilliseconds:0} ms."
        );
    }

    [Theory]
    [InlineData(null, 500)]
    [InlineData("1000", 1000)]
    public async Task An_ambient_playback_goes_AMBIENT_PREBUFFER_MS_ahead_of_real_time_over_about_100_ms_and_stays_that_far_ahead(
        string? setting,
        int milliseconds
    )
    {
        await using var harness = await ServerHarness.StartAsync(
            new Dictionary<string, string?> { ["AMBIENT_PREBUFFER_MS"] = setting }
        );
        var toilet = await harness.AddToiletAsync("0x0b0001");
        await harness.UploadAsync("Rain", await Tracks.WhiteNoiseAsync("wav", TimeSpan.FromSeconds(1)));

        await harness.PlayAmbientAsync(toilet);
        var packets = await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(2.6)));

        var prebuffer = TimeSpan.FromMilliseconds(milliseconds);
        var spread = TimeSpan.FromMilliseconds(100);
        // When the packet that starts the audio at `audio` arrived, after the first.
        TimeSpan Arrived(TimeSpan audio) => packets[AmbientAudio.Packets(audio)].ArrivedAfter(packets[0]);

        // The prebuffer goes out over the first 100 ms, beside the audio of those 100 ms: its end
        // 83 ms in at the default. Sent at once, it would arrive with the first packet; paced in
        // real time, the prebuffer's time later.
        Assert.InRange(Arrived(prebuffer), spread * 0.3, spread + TimeSpan.FromMilliseconds(150));
        Assert.InRange(Arrived(prebuffer + spread), spread * 0.3, spread + TimeSpan.FromMilliseconds(150));
        // From there the stream stays the prebuffer ahead, and never further.
        var later = TimeSpan.FromSeconds(2.5);
        Assert.InRange(
            Arrived(later),
            later - prebuffer - TimeSpan.FromMilliseconds(100),
            later - prebuffer + TimeSpan.FromMilliseconds(250)
        );
        var lead = packets
            .Select((packet, i) => i * TimeSpan.FromMilliseconds(10) - packet.ArrivedAfter(packets[0]))
            .Max();
        Assert.True(
            lead <= prebuffer + TimeSpan.FromMilliseconds(100),
            $"The stream went {lead.TotalMilliseconds:0} ms ahead of real time."
        );
    }

    [Fact]
    public async Task An_ambient_playback_fades_out_at_AMBIENT_CAP_SECONDS_counted_in_real_time_from_its_start()
    {
        await using var harness = await ServerHarness.StartAsync(
            new Dictionary<string, string?> { ["AMBIENT_CAP_SECONDS"] = "1" }
        );
        var toilet = await harness.AddToiletAsync("0x0b0001");
        var rain = await Tracks.WhiteNoiseAsync("wav", TimeSpan.FromSeconds(1));
        await harness.UploadAsync("Rain", rain);

        await harness.PlayAmbientAsync(toilet);
        var packets = await AmbientAudio.RestAsync(toilet);

        // A second after its start the stream is the prebuffer, half a second, ahead, and the
        // fade-out adds half a second more. A cap counted in audio sent would end it at 1.5 s.
        var samples = AmbientAudio.Samples(packets);
        Assert.InRange((double)samples.Length / DeviceApi.ToiletFormat.SampleRate, 1.9, 2.3);
        AmbientAudio.AssertOnePlayback(
            samples,
            await AmbientAudio.DecodedAsync(rain, DeviceApi.ToiletFormat),
            AmbientAudio.Fade,
            fadedOut: true
        );
        // The fade-out's last chunk, 20 ms of audio, goes out half a second after the cap.
        Assert.InRange(
            packets[^1].ArrivedAfter(packets[0]),
            TimeSpan.FromSeconds(1.4),
            TimeSpan.FromSeconds(1.75)
        );
        Assert.Null((await harness.AwaitDeviceAsync(toilet.DeviceId, _ => true)).Track);
    }
}

[CollectionDefinition(nameof(PacingTests), DisableParallelization = true)]
public sealed class PacingTestsCollection;
