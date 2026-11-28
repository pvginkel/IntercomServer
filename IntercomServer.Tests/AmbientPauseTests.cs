using System.Diagnostics;
using System.Text.Json.Nodes;
using IntercomServer.Tests.Harness;

namespace IntercomServer.Tests;

/// <summary>
/// What the server already sends a device, the doorbell, the ring and a ChatGPT reply, pauses its
/// ambient sound: the ambient stream sends nothing from that sound's start to its end, and then
/// resumes where it stopped, with the fade-in, on the device's counter. A ChatGPT conversation's
/// stream runs from its start to its end, between its replies too, and a sound that replaces
/// another keeps the pause from one to the other. A pause keeps the playback's track and its
/// switch, and any stop during it ends the playback with nothing more sent, the switch off at
/// once. In production only the doorbell, through <c>DOORBELL_EXTRA_DEVICES</c>, reaches a
/// device without a microphone, so the ring and the reply reach an intercom under the test's
/// <c>AMBIENT_TOPIC_PREFIXES</c>. The resume's lead is <c>PacingTests</c>'.
/// </summary>
public sealed class AmbientPauseTests
{
    private const string ToiletId = "0x0b0001";

    private static readonly TimeSpan TrackLength = TimeSpan.FromSeconds(1);

    // The frames of the default fade, half a second, at an intercom's 16 kHz.
    private const int IntercomFade = 8000;

    [Fact]
    public async Task The_doorbell_pauses_the_ambient_sound_which_keeps_its_track_and_switch_and_resumes_where_it_stopped_with_the_fade_in()
    {
        await using var harness = await ServerHarness.StartAsync(
            new Dictionary<string, string?> { ["DOORBELL_EXTRA_DEVICES"] = ToiletId }
        );
        var state = HomeAssistant.StateTopic(ToiletId);
        var observer = await harness.ObserveAsync(state);
        var toilet = await harness.AddToiletAsync(ToiletId);
        (await observer.NextAsync()).AssertIs(state, HomeAssistant.Off());
        var rain = await Tracks.WhiteNoiseAsync("wav", TrackLength);
        await harness.UploadAsync("Rain", rain);
        var track = await AmbientAudio.DecodedAsync(rain, DeviceApi.ToiletFormat);

        await harness.PlayAmbientAsync(toilet);
        (await observer.NextAsync()).AssertIs(state, HomeAssistant.On("Rain"));
        var packets = await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(0.8)));
        await harness.RingDoorbellAsync();
        // What the stream had sent ahead, then the doorbell's first second.
        packets.AddRange(await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(1.5))));

        // Paused, the playback keeps its track, and its switch stays on.
        Assert.Equal("Rain", (await harness.AwaitDeviceAsync(ToiletId, _ => true)).Track);
        (await harness.RetainedAsync(state)).AssertIs(state, HomeAssistant.On("Rain"));
        packets.AddRange(
            await toilet.NextPacketsAsync(
                AmbientAudio.Packets(BuiltInSounds.DoorbellLength + TimeSpan.FromSeconds(1.5 - 1))
            )
        );
        await harness.EndAmbientAsync(toilet);
        packets.AddRange(await AmbientAudio.RestAsync(toilet));
        (await observer.NextAsync()).AssertIs(state, HomeAssistant.Off());

        AmbientAudio.AssertNumbered(packets, first: 0);
        var doorbell = await BuiltInSounds.DecodedAsync(harness, Constants.AudioFiles.Doorbell, DeviceApi.ToiletFormat);
        var (start, end) = AmbientAudio.FindSound(packets, doorbell, looped: false);
        AssertWhole(packets, start, end, doorbell);
        AssertPausedAndResumed(packets, start, end, track, AmbientAudio.Fade);
    }

    [Fact]
    public async Task The_ring_pauses_the_ambient_sound_of_a_device_with_a_microphone_until_the_call_is_cancelled()
    {
        await using var harness = await ServerHarness.StartAsync(
            new Dictionary<string, string?> { ["AMBIENT_TOPIC_PREFIXES"] = TestDevice.IntercomPrefix }
        );
        var caller = await harness.AddDeviceAsync("0x0a0001");
        var called = await harness.AddDeviceAsync("0x0a0002");
        await caller.ComeOnlineAsync();
        await AmbientIntercomAsync(harness, called);
        var rain = await Tracks.WhiteNoiseAsync("wav", TrackLength);
        await harness.UploadAsync("Rain", rain);
        var track = await AmbientAudio.DecodedAsync(rain, LivePlayback.Format);

        await harness.PlayAmbientAsync(called);
        var packets = await called.NextPacketsAsync(IntercomPackets(TimeSpan.FromSeconds(0.8)));
        await Calls.RequestAsync(caller, called);
        // What the stream had sent ahead, then the ring's first second.
        packets.AddRange(await called.NextPacketsAsync(IntercomPackets(TimeSpan.FromSeconds(1.5))));
        await caller.ClickAsync();
        (await called.NextCommandAsync()).AssertIs("set/red_led", """{"state":"off"}""");
        packets.AddRange(await called.NextPacketsAsync(IntercomPackets(TimeSpan.FromSeconds(1.5))));
        await harness.EndAmbientAsync(called);
        packets.AddRange(await AmbientAudio.RestAsync(called));

        AmbientAudio.AssertNumbered(packets, first: 0);
        var ring = await BuiltInSounds.DecodedAsync(harness, Constants.AudioFiles.Ring, LivePlayback.Format);
        var (start, end) = AmbientAudio.FindSound(packets, ring, looped: true);
        AssertPausedAndResumed(packets, start, end, track, IntercomFade);
    }

    [Fact]
    public async Task A_sound_that_replaces_another_keeps_the_ambient_sound_paused_from_one_to_the_other()
    {
        // At 48 kHz, which no server decodes the built-in sounds in at its start, each sound's
        // decode lies between its start and its first packet: unpaused there, the ambient sound
        // would play between the ring and the doorbell that replaces it.
        await using var harness = await ServerHarness.StartAsync(
            new Dictionary<string, string?> { ["AMBIENT_TOPIC_PREFIXES"] = TestDevice.IntercomPrefix }
        );
        var caller = await harness.AddDeviceAsync("0x0a0001");
        var called = await harness.AddDeviceAsync("0x0a0002");
        await caller.ComeOnlineAsync();
        var configuration = called.DefaultConfiguration();
        configuration["audio_formats"]!["out"]!["sample_rate"] = DeviceApi.ToiletFormat.SampleRate;
        await AmbientIntercomAsync(harness, called, configuration);
        var rain = await Tracks.WhiteNoiseAsync("wav", TrackLength);
        await harness.UploadAsync("Rain", rain);
        var track = await AmbientAudio.DecodedAsync(rain, DeviceApi.ToiletFormat);

        await harness.PlayAmbientAsync(called);
        var packets = await called.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(0.8)));
        await Calls.RequestAsync(caller, called);
        packets.AddRange(await called.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(1))));
        await harness.RingDoorbellAsync();
        packets.AddRange(
            await called.NextPacketsAsync(AmbientAudio.Packets(BuiltInSounds.DoorbellLength + TimeSpan.FromSeconds(1.5)))
        );
        await harness.EndAmbientAsync(called);
        packets.AddRange(await AmbientAudio.RestAsync(called));

        AmbientAudio.AssertNumbered(packets, first: 0);
        var ring = await BuiltInSounds.DecodedAsync(harness, Constants.AudioFiles.Ring, DeviceApi.ToiletFormat);
        var doorbell = await BuiltInSounds.DecodedAsync(harness, Constants.AudioFiles.Doorbell, DeviceApi.ToiletFormat);
        var (ringStart, ringEnd) = AmbientAudio.FindSound(packets, ring, looped: true);
        var (doorbellStart, doorbellEnd) = AmbientAudio.FindSound(packets, doorbell, looped: false);
        Assert.True(
            doorbellStart == ringEnd,
            $"Packets {ringEnd} to {doorbellStart} lie between the ring's last and the doorbell's first."
        );
        AssertWhole(packets, doorbellStart, doorbellEnd, doorbell);
        AssertPausedAndResumed(packets, ringStart, doorbellEnd, track, AmbientAudio.Fade);
    }

    [Fact]
    public async Task A_ChatGPT_reply_s_stream_pauses_the_ambient_sound_from_its_start_to_its_end_between_its_replies_too()
    {
        await using var harness = await ServerHarness.StartAsync(
            new Dictionary<string, string?> { ["AMBIENT_TOPIC_PREFIXES"] = TestDevice.IntercomPrefix }
        );
        var device = await harness.AddDeviceAsync("0x0a0001");
        await AmbientIntercomAsync(harness, device);
        var rain = await Tracks.WhiteNoiseAsync("wav", TrackLength);
        await harness.UploadAsync("Rain", rain);
        var track = await AmbientAudio.DecodedAsync(rain, LivePlayback.Format);

        await harness.PlayAmbientAsync(device);
        var packets = await device.NextPacketsAsync(IntercomPackets(TimeSpan.FromSeconds(0.8)));
        packets.AddRange(device.TakeArrivedPackets());
        await using var reply = LivePlayback.Start(harness, device);
        var started = Stopwatch.GetTimestamp();

        // The stream pauses the ambient sound from its start, before it has audio to send: what
        // arrives in the second that follows was on its way at the start.
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        var arrived = device.TakeArrivedPackets();
        Assert.All(
            arrived,
            packet => Assert.True(
                Stopwatch.GetElapsedTime(started, packet.ReceivedAt) < TimeSpan.FromSeconds(0.5),
                $"An ambient packet arrived {Stopwatch.GetElapsedTime(started, packet.ReceivedAt).TotalMilliseconds:0} ms after the reply's stream started."
            )
        );
        packets.AddRange(arrived);

        // Two replies, a second of silence between them as between two turns.
        reply.Append(10);
        packets.AddRange(await device.NextPacketsAsync(10));
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        reply.Append(10);
        packets.AddRange(await device.NextPacketsAsync(10));
        await reply.CompleteAsync();
        packets.AddRange(await device.NextPacketsAsync(IntercomPackets(TimeSpan.FromSeconds(1.5))));
        await harness.EndAmbientAsync(device);
        packets.AddRange(await AmbientAudio.RestAsync(device));

        AmbientAudio.AssertNumbered(packets, first: 0);
        var replies = new byte[20 * LivePlayback.ChunkBytes];
        var (start, end) = AmbientAudio.FindSound(packets, replies, looped: false);
        AssertWhole(packets, start, end, replies);
        AssertPausedAndResumed(packets, start, end, track, IntercomFade);
    }

    [Theory]
    [InlineData("switch")]
    [InlineData("routes")]
    [InlineData("cap")]
    [InlineData("offline")]
    [InlineData("removal")]
    [InlineData("shutdown")]
    public async Task A_stop_during_the_pause_ends_the_playback_with_nothing_more_sent_and_turns_the_switch_off_at_once(
        string stop
    )
    {
        // The cap, counted from the sound's start, comes during the doorbell.
        var settings = new Dictionary<string, string?> { ["DOORBELL_EXTRA_DEVICES"] = ToiletId };
        if (stop == "cap")
            settings["AMBIENT_CAP_SECONDS"] = "2";
        await using var harness = await ServerHarness.StartAsync(settings);
        var state = HomeAssistant.StateTopic(ToiletId);
        var observer = await harness.ObserveAsync(state);
        var toilet = await harness.AddToiletAsync(ToiletId);
        (await observer.NextAsync()).AssertIs(state, HomeAssistant.Off());
        var rain = await Tracks.WhiteNoiseAsync("wav", TrackLength);
        await harness.UploadAsync("Rain", rain);
        var track = await AmbientAudio.DecodedAsync(rain, DeviceApi.ToiletFormat);
        var doorbell = await BuiltInSounds.DecodedAsync(harness, Constants.AudioFiles.Doorbell, DeviceApi.ToiletFormat);

        await harness.PlayAmbientAsync(toilet);
        (await observer.NextAsync()).AssertIs(state, HomeAssistant.On("Rain"));
        var packets = await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(0.6)));
        await harness.RingDoorbellAsync();
        packets.AddRange(await AmbientAudio.UntilSoundAsync(toilet, doorbell));

        await (
            stop switch
            {
                "switch" => harness.SwitchAsync(ToiletId, "OFF"),
                "routes" => harness.EndAmbientAsync(toilet),
                "offline" => toilet.PublishStateAsync(online: false),
                // Its configuration kept, the device still has an endpoint to send to.
                "removal" => toilet.PublishAsync("state", null, retain: true),
                "shutdown" => harness.StopServerAsync(),
                _ => Task.CompletedTask,
            }
        );
        // A removed device's state is emptied, and its playback's end publishes nothing.
        if (stop == "removal")
            (await observer.NextAsync()).AssertIs(state, "");
        else
            (await observer.NextAsync()).AssertIs(state, HomeAssistant.Off());
        var off = Stopwatch.GetTimestamp();
        packets.AddRange(await AmbientAudio.RestAsync(toilet));

        AmbientAudio.AssertNumbered(packets, first: 0);
        var (start, end) = AmbientAudio.FindSound(packets, doorbell, looped: false);
        AssertWhole(packets, start, end, doorbell);
        Assert.True(end == packets.Count, $"{packets.Count - end} packets followed the doorbell.");
        Assert.True(packets[^1].ReceivedAt > off, "The switch went off only once the doorbell had ended.");
        AmbientAudio.AssertOnePlayback(
            AmbientAudio.Samples(packets.Take(start)),
            track,
            AmbientAudio.Fade,
            fadedOut: false
        );
    }

    // Connects an intercom whose ambient sound the routes start, and returns once the server has read it.
    private static async Task AmbientIntercomAsync(
        ServerHarness harness,
        TestDevice device,
        JsonObject? configuration = null
    )
    {
        await device.PublishConfigurationAsync(configuration ?? device.DefaultConfiguration());
        await device.PublishStateAsync();
        await harness.AwaitDeviceAsync(device.DeviceId, listed => listed.Online);
    }

    // The datagrams of `length` of audio to an intercom: one each 20 ms.
    private static int IntercomPackets(TimeSpan length) => (int)(length / LivePlayback.ChunkLength);

    // Asserts packets[start..end] carry the whole of `sound`, and nothing else.
    private static void AssertWhole(List<AudioPacket> packets, int start, int end, byte[] sound) =>
        Assert.Equal(sound.Length, packets.Skip(start).Take(end - start).Sum(packet => packet.Payload.Length));

    // Asserts that the ambient sound's packets around packets[start..end], another sound's, are
    // one playback of `track` that stopped where that sound began, at full level, and resumed
    // where it stopped, with the fade-in, at the same gain, and faded out at its stop.
    private static void AssertPausedAndResumed(List<AudioPacket> packets, int start, int end, short[] track, int fade)
    {
        var before = AmbientAudio.Samples(packets.Take(start));
        var after = AmbientAudio.Samples(packets.Skip(end));
        var gain = AmbientAudio.AssertOnePlayback(before, track, fade, fadedOut: false);
        var resumed = AmbientAudio.AssertOnePlayback(after, track, fade, fadedOut: true, from: before.Length);
        Assert.InRange(resumed, gain - 0.01, gain + 0.01);
    }
}
