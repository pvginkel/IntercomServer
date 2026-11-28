using System.Diagnostics;
using System.Net;
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
/// ahead, and its cap, counted in real time from its start, fades it out. One that starts while
/// the device still holds what was sent before tops it up to the prebuffer, and so does one that
/// resumes after another sound paused it, counting what the device still holds of that sound. An
/// occasional sound goes in real time, one at a time for the whole house, each after a wait drawn
/// from the end of the one before. The class runs alone, after the suite's other tests, so their
/// servers do not stretch it too.
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_start_soon_after_a_stop_tops_the_device_up_to_AMBIENT_PREBUFFER_MS_and_no_further(bool duringFadeOut)
    {
        await using var harness = await ServerHarness.StartAsync();
        var toilet = await harness.AddToiletAsync("0x0b0001");
        await harness.UploadAsync("Rain", await Tracks.WhiteNoiseAsync("wav", TimeSpan.FromSeconds(1)));
        var token = harness.UserToken();

        await harness.PlayAmbientAsync(toilet);
        var packets = await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(1.2)));
        if (duringFadeOut)
        {
            // The start waits out the fade-out, so its sound begins as the fade-out's last chunk
            // goes. Until the stop has reached the player, a start joins the playback it stops.
            var stopping = harness.StopAmbientAsync(token, toilet.DeviceId);
            while (true)
            {
                using var refused = await harness.StartAmbientAsync(token, toilet.DeviceId, "Nope");
                if (refused.StatusCode == HttpStatusCode.NotFound)
                    break;
                Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
            }
            await harness.PlayAmbientAsync(toilet);
            using var stopped = await stopping;
            Assert.Equal(HttpStatusCode.NoContent, stopped.StatusCode);
        }
        else
        {
            await harness.EndAmbientAsync(toilet);
            await harness.PlayAmbientAsync(toilet);
        }
        packets.AddRange(toilet.TakeArrivedPackets());
        packets.AddRange(await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(1.5))));

        // What the device holds as each packet arrives, playing what it holds in real time: what
        // it held at the packet before, less the time since, and the packet's audio. A fresh
        // prebuffer on top of what the first playback left would hold about twice the prebuffer.
        var held = new List<double>();
        for (var i = 0; i < packets.Count; i++)
        {
            var left = i == 0 ? 0 : Math.Max(0, held[^1] - packets[i].ArrivedAfter(packets[i - 1]).TotalSeconds);
            held.Add(left + (double)packets[i].Payload.Length / 2 / DeviceApi.ToiletFormat.SampleRate);
        }
        const double prebuffer = 0.5;
        Assert.True(held.Max() <= prebuffer + 0.1, $"The device held {held.Max() * 1000:0} ms.");
        var end = held.TakeLast(AmbientAudio.Packets(TimeSpan.FromSeconds(0.5))).Max();
        Assert.True(end >= prebuffer - 0.1, $"The second playback kept the device {end * 1000:0} ms ahead.");
    }

    [Fact]
    public async Task After_a_doorbell_the_ambient_sound_resumes_with_the_device_never_more_than_AMBIENT_PREBUFFER_MS_ahead()
    {
        await using var harness = await ServerHarness.StartAsync(
            new Dictionary<string, string?> { ["DOORBELL_EXTRA_DEVICES"] = "0x0b0001" }
        );
        var toilet = await harness.AddToiletAsync("0x0b0001");
        await harness.UploadAsync("Rain", await Tracks.WhiteNoiseAsync("wav", TimeSpan.FromSeconds(1)));
        // Decoded ahead, the doorbell's first packet goes as the ambient sound pauses.
        await BuiltInSounds.DecodedAsync(harness, Constants.AudioFiles.Doorbell, DeviceApi.ToiletFormat);

        await harness.PlayAmbientAsync(toilet);
        var packets = await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(1)));
        await harness.RingDoorbellAsync();
        packets.AddRange(
            await toilet.NextPacketsAsync(AmbientAudio.Packets(BuiltInSounds.DoorbellLength + TimeSpan.FromSeconds(2.5)))
        );

        // How far the audio the device has received, from the playback's first packet, through
        // the doorbell and on past the resume, runs ahead of the time passed. The doorbell plays
        // in real time behind the lead the ambient sound had sent, so the device still holds that
        // lead at its end, and a fresh prebuffer on top would put it about twice as far ahead.
        var received = 0.0;
        var ahead = new List<double>();
        foreach (var packet in packets)
        {
            received += (double)packet.Payload.Length / 2 / DeviceApi.ToiletFormat.SampleRate;
            ahead.Add(received - packet.ArrivedAfter(packets[0]).TotalSeconds);
        }
        const double prebuffer = 0.5;
        Assert.True(ahead.Max() <= prebuffer + 0.1, $"The device was {ahead.Max() * 1000:0} ms ahead.");
        var end = ahead.TakeLast(AmbientAudio.Packets(TimeSpan.FromSeconds(0.5))).Min();
        Assert.True(end >= prebuffer - 0.1, $"The resumed playback kept the device {end * 1000:0} ms ahead.");
    }

    [Fact]
    public async Task After_a_pause_the_device_s_buffer_drained_in_the_ambient_sound_resumes_with_its_prebuffer_spread_over_about_100_ms()
    {
        await using var harness = await ServerHarness.StartAsync(
            new Dictionary<string, string?> { ["AMBIENT_TOPIC_PREFIXES"] = TestDevice.IntercomPrefix }
        );
        var device = await harness.AddDeviceAsync("0x0a0001");
        await device.ComeOnlineAsync();
        await harness.AwaitDeviceAsync(device.DeviceId, listed => listed.Online);
        await harness.UploadAsync("Rain", await Tracks.WhiteNoiseAsync("wav", TimeSpan.FromSeconds(1)));

        await harness.PlayAmbientAsync(device);
        var packets = await device.NextPacketsAsync(30);
        // A ChatGPT conversation whose reply ends long before the conversation does.
        await using (var reply = LivePlayback.Start(harness, device))
        {
            reply.Append(10);
            await Task.Delay(TimeSpan.FromSeconds(1.5), TestContext.Current.CancellationToken);
            await reply.CompleteAsync();
        }
        // The reply and about two seconds of the resumed sound, a packet each 20 ms. The stream sent
        // its whole lead in its first 100 ms, so only a packet or two of it came after the 30 taken.
        packets.AddRange(await device.NextPacketsAsync(10 + 100));
        var (_, end) = AmbientAudio.FindSound(packets, new byte[10 * LivePlayback.ChunkBytes], looped: false);
        var resumed = packets.Skip(end).ToList();

        // The resume sends the prebuffer over its first 100 ms, its end 83 ms in. Counted from the
        // stream's start, the time paused would be owed and go out at once; with no lead, the
        // prebuffer would arrive in its own time.
        var prebuffer = TimeSpan.FromMilliseconds(500);
        var spread = TimeSpan.FromMilliseconds(100);
        var arrived = resumed[(int)(prebuffer / LivePlayback.ChunkLength)].ArrivedAfter(resumed[0]);
        Assert.InRange(arrived, spread * 0.3, spread + TimeSpan.FromMilliseconds(150));
        var lead = resumed
            .Select((packet, i) => i * LivePlayback.ChunkLength - packet.ArrivedAfter(resumed[0]))
            .Max();
        Assert.True(
            lead <= prebuffer + TimeSpan.FromMilliseconds(100),
            $"The resumed stream went {lead.TotalMilliseconds:0} ms ahead of real time."
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

    [Fact]
    public async Task Occasional_sounds_play_in_real_time_one_at_a_time_each_after_a_wait_from_OCCASIONAL_WAIT_MIN_SECONDS_to_OCCASIONAL_WAIT_MAX_SECONDS()
    {
        var settings = await OccasionalSounds.SettingsAsync();
        settings["OCCASIONAL_WAIT_MAX_SECONDS"] = "2";
        await using var harness = await ServerHarness.StartAsync(settings);
        TestDevice[] speakers = [await harness.AddDeviceAsync("0x0a0001"), await harness.AddDeviceAsync("0x0a0002")];
        foreach (var speaker in speakers)
            await speaker.ComeOnlineAsync();
        // Longer than the longest wait, so waits counted from each moment's start would overlap
        // two sounds.
        await harness.UploadAsync("Swell", await Tracks.WhiteNoiseAsync("wav", TimeSpan.FromSeconds(2.5)));

        await harness.SetOccasionalAsync("Swell");
        var heard = await Task.WhenAll(speakers.Select(speaker => SoundsAsync(speaker, TimeSpan.FromSeconds(16))));

        var sounds = heard.SelectMany(sounds => sounds).OrderBy(sound => sound[0].ReceivedAt).ToList();
        Assert.True(sounds.Count >= 3, $"{sounds.Count} sounds played in 16 s.");
        foreach (var sound in sounds)
        {
            var audio = TimeSpan.FromSeconds(
                (double)sound.SkipLast(1).Sum(packet => packet.Payload.Length) / LivePlayback.Format.BytesPerSecond
            );
            Assert.InRange(sound[^1].ArrivedAfter(sound[0]), audio * 0.9, audio + TimeSpan.FromMilliseconds(250));
        }
        // The wait goes from a sound's last chunk to the next one's first, the decode within it.
        for (var i = 1; i < sounds.Count; i++)
            Assert.InRange(
                sounds[i][0].ArrivedAfter(sounds[i - 1][^1]),
                TimeSpan.FromSeconds(1) - TimeSpan.FromMilliseconds(20),
                TimeSpan.FromSeconds(2.5)
            );
    }

    [Fact]
    public async Task Each_wait_is_drawn_anew_so_some_end_in_the_first_half_of_OCCASIONAL_WAIT_MIN_SECONDS_to_OCCASIONAL_WAIT_MAX_SECONDS_and_some_in_the_second()
    {
        var settings = await OccasionalSounds.SettingsAsync();
        settings["OCCASIONAL_WAIT_MAX_SECONDS"] = "3";
        await using var harness = await ServerHarness.StartAsync(settings);
        var intercom = await harness.AddDeviceAsync("0x0a0001");
        await intercom.ComeOnlineAsync();
        await harness.UploadAsync("Chime", await Tracks.WhiteNoiseAsync("wav", TimeSpan.FromSeconds(0.1)));

        await harness.SetOccasionalAsync("Chime");
        // A gap is its wait and the moment's decode, well under a second: a wait drawn from 1 s to
        // 3 s ends either side of 2 s, a fixed one always on the same side.
        var halfway = TimeSpan.FromSeconds(2);
        var gaps = new List<TimeSpan>();
        var before = await OccasionalSounds.NextSoundAsync(intercom);
        while (!(gaps.Any(gap => gap < halfway) && gaps.Any(gap => gap >= halfway)))
        {
            Assert.True(
                gaps.Count < 20,
                $"Twenty gaps on one side of 2 s: {string.Join(", ", gaps.Select(gap => $"{gap.TotalSeconds:0.00} s"))}."
            );
            var sound = await OccasionalSounds.NextSoundAsync(intercom);
            gaps.Add(sound[0].ArrivedAfter(before[^1]));
            before = sound;
        }
    }

    // The sounds whose first packet arrives at the device within `during`, each its packets until
    // none arrives for AmbientAudio.Quiet.
    private static async Task<List<List<AudioPacket>>> SoundsAsync(TestDevice device, TimeSpan during)
    {
        var sounds = new List<List<AudioPacket>>();
        var stopwatch = Stopwatch.StartNew();
        while (during - stopwatch.Elapsed is { Ticks: > 0 } left && await device.NextPacketWithinAsync(left) is { } first)
        {
            var sound = new List<AudioPacket> { first };
            sound.AddRange(await AmbientAudio.RestAsync(device));
            sounds.Add(sound);
        }
        return sounds;
    }
}

[CollectionDefinition(nameof(PacingTests), DisableParallelization = true)]
public sealed class PacingTestsCollection;
