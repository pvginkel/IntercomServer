using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using IntercomServer.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace IntercomServer.Tests;

/// <summary>
/// The occasional player, driven on the real clock through its settings, a wait of a second and
/// a window set around the test's own time, and heard at the devices' UDP listeners. At a moment
/// inside the window, with an Occasional track, it plays one on a speaker drawn among the idle
/// ones: any device the server knows, an intercom or a toilet, online, advertising an endpoint
/// and a format the server can send, and not excluded; idle with no ambient playback, no other
/// sound of the server's and no ChatGPT reply. The sound is the track decoded into the speaker's
/// format, levelled to <c>OCCASIONAL_LOUDNESS_LUFS</c> and played once on the device's counter,
/// from its start to its end, and it ends early once its device goes offline, is removed or
/// advertises no endpoint or another format, and at the server's shutdown. Each Occasional track
/// plays once a round, a round reads the store at each moment, and a new round's first is not the
/// last one played. A moment with no Occasional track, or outside the window, plays nothing and
/// logs nothing; one with no idle speaker is logged. The waits are <c>PacingTests</c>'.
/// </summary>
public sealed class OccasionalPlayerTests
{
    private const string Skipped = "No speaker is idle, so this moment plays no occasional sound";

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_sound_is_an_Occasional_track_decoded_into_its_speaker_s_format_levelled_to_OCCASIONAL_LOUDNESS_LUFS_and_played_once_from_its_start_to_its_end()
    {
        using var log = ServerLog.Capture();
        var settings = await OccasionalSounds.SettingsAsync();
        settings["OCCASIONAL_LOUDNESS_LUFS"] = "-26";
        await using var harness = await ServerHarness.StartAsync(settings);
        // An intercom, under a prefix AMBIENT_TOPIC_PREFIXES does not name.
        var intercom = await harness.AddDeviceAsync("0x0a0001");
        await intercom.ComeOnlineAsync();
        var chime = await Tracks.WhiteNoiseAsync("wav", TimeSpan.FromSeconds(0.5));
        await harness.UploadAsync("Chime", chime);
        var track = await AmbientAudio.DecodedAsync(chime, LivePlayback.Format);
        var measured = await AmbientAudio.MeasuredAsync(chime, LivePlayback.Format);

        await harness.SetOccasionalAsync("Chime");
        var sound = await OccasionalSounds.NextSoundAsync(intercom);

        AmbientAudio.AssertNumbered(sound, first: 0);
        Assert.All(sound.SkipLast(1), packet => Assert.Equal(LivePlayback.ChunkBytes, packet.Payload.Length));
        var samples = AmbientAudio.Samples(sound);
        Assert.Equal(track.Length, samples.Length);
        // At full level from its first sample to its last: no fade.
        var gain = AmbientAudio.AssertOnePlayback(samples, track, fade: 0, fadedOut: false);
        // The noise's peak, about −12 dBFS, stays well below −1 dBFS at this gain.
        var expected = -26 - measured.Loudness;
        Assert.InRange(gain, expected - 0.01, expected + 0.01);
        // The first two: the next moment plays it again.
        Assert.Equal(
            [
                $"Playing occasional track Chime on device 0x0a0001 at a gain of {expected.ToString("0.0", CultureInfo.InvariantCulture)} dB",
                "Ended occasional track Chime on device 0x0a0001",
            ],
            log.Messages<OccasionalPlayer>().Take(2)
        );
    }

    [Fact]
    public async Task With_no_Occasional_track_no_sound_plays_and_the_player_logs_nothing()
    {
        using var log = ServerLog.Capture();
        await using var harness = await ServerHarness.StartAsync(await OccasionalSounds.SettingsAsync());
        var intercom = await harness.AddDeviceAsync("0x0a0001");
        await intercom.ComeOnlineAsync();
        await harness.UploadAsync("Rain", await Tracks.WhiteNoiseAsync("wav", TimeSpan.FromSeconds(0.5)));
        var snow = await Tracks.WhiteNoiseAsync("wav", TimeSpan.FromSeconds(0.5), seed: 2);
        await harness.UploadAsync("Snow", snow);
        Assert.NotNull((await harness.TrackStore().UpdateAsync("Snow", null, TrackState.Disabled, Cancellation)).Track);

        // Three moments with an Ambient track and a Disabled one.
        await Task.Delay(TimeSpan.FromSeconds(3.5), Cancellation);
        Assert.Equal(0, intercom.ReceivedPacketCount);
        Assert.Empty(log.Messages<OccasionalPlayer>());

        // The moments come on: the first after a track is set Occasional plays it.
        await harness.SetOccasionalAsync("Snow");
        var sound = await OccasionalSounds.NextSoundAsync(intercom);
        AmbientAudio.AssertOnePlayback(
            AmbientAudio.Samples(sound),
            await AmbientAudio.DecodedAsync(snow, LivePlayback.Format),
            fade: 0,
            fadedOut: false
        );
    }

    [Theory]
    [InlineData(nameof(OccasionalWindow.Later))]
    [InlineData(nameof(OccasionalWindow.Ended))]
    public async Task No_sound_starts_outside_the_window_and_the_player_logs_nothing(string window)
    {
        using var log = ServerLog.Capture();
        await using var outside = await ServerHarness.StartAsync(
            await OccasionalSounds.SettingsAsync(Enum.Parse<OccasionalWindow>(window))
        );
        // A server beside it whose window holds: its moments come as the other's do.
        await using var inside = await ServerHarness.StartAsync(await OccasionalSounds.SettingsAsync());
        var unheard = await outside.AddDeviceAsync("0x0a0001");
        var heard = await inside.AddDeviceAsync("0x0a0002");
        var chime = await Tracks.WhiteNoiseAsync("wav", TimeSpan.FromSeconds(0.3));
        foreach (var (harness, device) in new[] { (outside, unheard), (inside, heard) })
        {
            await device.ComeOnlineAsync();
            await harness.UploadAsync("Chime", chime);
            await harness.SetOccasionalAsync("Chime");
        }

        // The other server's track was set first, and each of these follows a moment of its own.
        await OccasionalSounds.NextSoundAsync(heard);
        await OccasionalSounds.NextSoundAsync(heard);

        Assert.Equal(0, unheard.ReceivedPacketCount);
        Assert.All(log.Messages<OccasionalPlayer>(), message => Assert.Contains("on device 0x0a0002", message));
    }

    [Theory]
    [InlineData("07:59:59.999", false)]
    [InlineData("08:00", true)]
    [InlineData("20:59:59.999", true)]
    [InlineData("21:00", false)]
    public void The_window_holds_from_08_00_and_ends_before_21_00_by_default(string time, bool holds)
    {
        Assert.Equal(holds, new OccasionalConfiguration().WindowHolds(TimeOnly.Parse(time, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task The_settings_are_read_as_set_and_an_unset_or_empty_one_takes_its_default()
    {
        await using (
            var harness = await ServerHarness.StartAsync(
                new Dictionary<string, string?>
                {
                    ["OCCASIONAL_WINDOW_START"] = "09:30",
                    ["OCCASIONAL_WINDOW_END"] = "18:45",
                    ["OCCASIONAL_WAIT_MIN_SECONDS"] = "60",
                    ["OCCASIONAL_WAIT_MAX_SECONDS"] = "90",
                    ["OCCASIONAL_EXCLUDED_DEVICES"] = " 0x0a0001 , 0x0b0001",
                    ["OCCASIONAL_LOUDNESS_LUFS"] = "-23.5",
                }
            )
        )
        {
            var configuration = harness.Services.GetRequiredService<OccasionalConfiguration>();
            Assert.Equal(
                (new TimeOnly(9, 30), new TimeOnly(18, 45), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(90), -23.5),
                (
                    configuration.WindowStart,
                    configuration.WindowEnd,
                    configuration.ShortestWait,
                    configuration.LongestWait,
                    configuration.Loudness
                )
            );
            Assert.Equal(["0x0a0001", "0x0b0001"], configuration.ExcludedDevices);
        }

        foreach (var unset in new[] { null, "" })
        {
            await using var harness = await ServerHarness.StartAsync(
                new Dictionary<string, string?>
                {
                    ["OCCASIONAL_WINDOW_START"] = unset,
                    ["OCCASIONAL_WINDOW_END"] = unset,
                    ["OCCASIONAL_WAIT_MIN_SECONDS"] = unset,
                    ["OCCASIONAL_WAIT_MAX_SECONDS"] = unset,
                    ["OCCASIONAL_EXCLUDED_DEVICES"] = unset,
                    ["OCCASIONAL_LOUDNESS_LUFS"] = unset,
                }
            );
            var configuration = harness.Services.GetRequiredService<OccasionalConfiguration>();
            Assert.Equal(
                (new TimeOnly(8, 0), new TimeOnly(21, 0), TimeSpan.FromSeconds(1200), TimeSpan.FromSeconds(1800), -20.0),
                (
                    configuration.WindowStart,
                    configuration.WindowEnd,
                    configuration.ShortestWait,
                    configuration.LongestWait,
                    configuration.Loudness
                )
            );
            Assert.Empty(configuration.ExcludedDevices);
        }
    }

    [Theory]
    [InlineData("OCCASIONAL_WINDOW_START=8am", "OCCASIONAL_WINDOW_START")]
    [InlineData("OCCASIONAL_WINDOW_START=24:00", "OCCASIONAL_WINDOW_START")]
    [InlineData("OCCASIONAL_WINDOW_END=21.00", "OCCASIONAL_WINDOW_END")]
    [InlineData("OCCASIONAL_WINDOW_START=21:00;OCCASIONAL_WINDOW_END=08:00", "OCCASIONAL_WINDOW_START")]
    [InlineData("OCCASIONAL_WINDOW_START=10:00;OCCASIONAL_WINDOW_END=10:00", "OCCASIONAL_WINDOW_START")]
    [InlineData("OCCASIONAL_WAIT_MIN_SECONDS=0", "OCCASIONAL_WAIT_MIN_SECONDS")]
    [InlineData("OCCASIONAL_WAIT_MIN_SECONDS=1.5", "OCCASIONAL_WAIT_MIN_SECONDS")]
    [InlineData("OCCASIONAL_WAIT_MIN_SECONDS=twenty", "OCCASIONAL_WAIT_MIN_SECONDS")]
    [InlineData("OCCASIONAL_WAIT_MIN_SECONDS=60;OCCASIONAL_WAIT_MAX_SECONDS=59", "OCCASIONAL_WAIT_MAX_SECONDS")]
    [InlineData("OCCASIONAL_WAIT_MAX_SECONDS=4294968", "OCCASIONAL_WAIT_MAX_SECONDS")]
    [InlineData("OCCASIONAL_WAIT_MAX_SECONDS=30m", "OCCASIONAL_WAIT_MAX_SECONDS")]
    [InlineData("OCCASIONAL_EXCLUDED_DEVICES=0x0a0001,", "OCCASIONAL_EXCLUDED_DEVICES")]
    [InlineData("OCCASIONAL_EXCLUDED_DEVICES=intercom/client/0x0a0001", "OCCASIONAL_EXCLUDED_DEVICES")]
    [InlineData("OCCASIONAL_LOUDNESS_LUFS=-20 LUFS", "OCCASIONAL_LOUDNESS_LUFS")]
    [InlineData("OCCASIONAL_LOUDNESS_LUFS=NaN", "OCCASIONAL_LOUDNESS_LUFS")]
    public async Task A_setting_not_of_its_form_or_breaking_its_rule_stops_the_server_s_start_naming_it(string settings, string named)
    {
        var values = settings.Split(';').Select(setting => setting.Split('=')).ToDictionary(pair => pair[0], pair => (string?)pair[1]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => ServerHarness.StartAsync(values));

        Assert.StartsWith($"{named} ", error.Message);
    }

    [Fact]
    public async Task Every_speaker_may_be_picked_an_intercom_and_a_toilet_alike_enabled_or_not_and_never_a_device_that_is_no_speaker_or_is_excluded()
    {
        using var log = ServerLog.Capture();
        var settings = await OccasionalSounds.SettingsAsync();
        settings["OCCASIONAL_EXCLUDED_DEVICES"] = "0x0a0003";
        await using var harness = await ServerHarness.StartAsync(settings);
        var disabled = await harness.AddDeviceAsync("0x0a0001");
        await disabled.PublishConfigurationAsync();
        await disabled.PublishStateAsync(enabled: false);
        var toilet = await harness.AddToiletAsync("0x0b0001");
        var excluded = await harness.AddDeviceAsync("0x0a0003");
        await excluded.ComeOnlineAsync();
        var offline = await harness.AddDeviceAsync("0x0a0004");
        await offline.PublishConfigurationAsync();
        await offline.PublishStateAsync(online: false);
        var noEndpoint = await harness.AddDeviceAsync("0x0a0005");
        var withoutEndpoint = noEndpoint.DefaultConfiguration();
        withoutEndpoint.Remove("endpoint");
        await noEndpoint.PublishConfigurationAsync(withoutEndpoint);
        await noEndpoint.PublishStateAsync();
        var stereo = await harness.AddDeviceAsync("0x0a0006");
        var stereoOut = stereo.DefaultConfiguration();
        stereoOut["audio_formats"]!["out"]!["channel_layout"] = "stereo";
        await stereo.PublishConfigurationAsync(stereoOut);
        await stereo.PublishStateAsync();
        await harness.AwaitDeviceAsync(stereo.DeviceId, device => device.Online);
        await harness.UploadAsync("Chime", await Tracks.WhiteNoiseAsync("wav", TimeSpan.FromSeconds(0.2)));

        await harness.SetOccasionalAsync("Chime");
        // Each speaker gets a sound before long, whichever the draws pick first.
        await OccasionalSounds.NextSoundAsync(disabled);
        await OccasionalSounds.NextSoundAsync(toilet);

        foreach (var device in new[] { excluded, offline, noEndpoint, stereo })
        {
            Assert.Equal(0, device.ReceivedPacketCount);
            Assert.DoesNotContain(log.Messages<OccasionalPlayer>(), message => message.Contains(device.DeviceId));
        }
    }

    [Fact]
    public async Task Each_Occasional_track_plays_once_in_a_round_and_a_new_round_s_first_is_not_the_last_one_played()
    {
        await using var harness = await ServerHarness.StartAsync(await OccasionalSounds.SettingsAsync());
        var intercom = await harness.AddDeviceAsync("0x0a0001");
        await intercom.ComeOnlineAsync();
        var tracks = new Dictionary<string, short[]>();
        foreach (var (name, seed) in new[] { ("A", 1), ("B", 2), ("C", 3) })
        {
            var noise = await Tracks.WhiteNoiseAsync("wav", TimeSpan.FromSeconds(0.3), seed);
            await harness.UploadAsync(name, noise);
            tracks[name] = await AmbientAudio.DecodedAsync(noise, LivePlayback.Format);
        }

        foreach (var name in tracks.Keys)
            await harness.SetOccasionalAsync(name);
        var played = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var sound = await OccasionalSounds.NextSoundAsync(intercom);
            played.Add(OccasionalSounds.Which(sound, tracks));
            AmbientAudio.AssertOnePlayback(AmbientAudio.Samples(sound), tracks[played[^1]], fade: 0, fadedOut: false);
        }

        Assert.Equal(["A", "B", "C"], played[..3].Order());
        Assert.Equal(["A", "B", "C"], played[3..].Order());
        Assert.NotEqual(played[2], played[3]);
    }

    [Fact]
    public async Task A_track_set_to_Occasional_mid_round_joins_it_and_one_set_to_another_state_leaves_it()
    {
        await using var harness = await ServerHarness.StartAsync(await OccasionalSounds.SettingsAsync());
        var intercom = await harness.AddDeviceAsync("0x0a0001");
        await intercom.ComeOnlineAsync();
        var tracks = new Dictionary<string, short[]>();
        foreach (var (name, seed) in new[] { ("A", 1), ("B", 2), ("C", 3) })
        {
            var noise = await Tracks.WhiteNoiseAsync("wav", TimeSpan.FromSeconds(1), seed);
            await harness.UploadAsync(name, noise);
            tracks[name] = await AmbientAudio.DecodedAsync(noise, LivePlayback.Format);
        }

        await harness.SetOccasionalAsync("A");
        await harness.SetOccasionalAsync("B");
        // While the round's first plays: C joins, and the one still due leaves.
        var first = OccasionalSounds.Which([await intercom.NextPacketAsync()], tracks);
        await harness.SetOccasionalAsync("C");
        var due = first == "A" ? "B" : "A";
        Assert.NotNull((await harness.TrackStore().UpdateAsync(due, null, TrackState.Disabled, Cancellation)).Track);
        await AmbientAudio.RestAsync(intercom);

        var second = OccasionalSounds.Which(await OccasionalSounds.NextSoundAsync(intercom), tracks);
        var third = OccasionalSounds.Which(await OccasionalSounds.NextSoundAsync(intercom), tracks);

        // C ends the round, and the next one's first is the other Occasional track.
        Assert.Equal(("C", first), (second, third));
    }

    [Theory]
    [InlineData("undecodable")]
    [InlineData("empty")]
    public async Task A_track_that_does_not_decode_or_decodes_to_no_audio_is_logged_and_its_turn_counts_as_played(string broken)
    {
        using var log = ServerLog.Capture();
        await using var harness = await ServerHarness.StartAsync(await OccasionalSounds.SettingsAsync());
        var intercom = await harness.AddDeviceAsync("0x0a0001");
        await intercom.ComeOnlineAsync();
        await harness.UploadAsync("Broken", broken == "undecodable" ? Tracks.UndecodableWav() : await Tracks.EmptyFlacAsync());
        var chime = await Tracks.WhiteNoiseAsync("wav", TimeSpan.FromSeconds(0.3));
        await harness.UploadAsync("Chime", chime);
        var track = await AmbientAudio.DecodedAsync(chime, LivePlayback.Format);

        await harness.SetOccasionalAsync("Broken");
        await harness.SetOccasionalAsync("Chime");
        // A round of each, whichever comes first in it: a turn Broken did not count would leave it
        // the round's last for good.
        for (var round = 0; round < 2; round++)
            AmbientAudio.AssertOnePlayback(
                AmbientAudio.Samples(await OccasionalSounds.NextSoundAsync(intercom)),
                track,
                fade: 0,
                fadedOut: false
            );

        Assert.Contains(
            log.Messages<OccasionalPlayer>(),
            message => message.StartsWith("The occasional track Broken does not play on device 0x0a0001: ")
        );
    }

    [Fact]
    public async Task A_speaker_with_an_ambient_playback_is_not_picked_and_the_moment_skipped_is_logged_until_the_playback_has_ended()
    {
        using var log = ServerLog.Capture();
        await using var harness = await ServerHarness.StartAsync(await OccasionalSounds.SettingsAsync());
        var toilet = await harness.AddToiletAsync("0x0b0001");
        var rain = await Tracks.WhiteNoiseAsync("wav", TimeSpan.FromSeconds(1));
        var chime = await Tracks.WhiteNoiseAsync("wav", TimeSpan.FromSeconds(0.5), seed: 2);
        await harness.UploadAsync("Rain", rain);
        await harness.UploadAsync("Chime", chime);
        var chimeTrack = await AmbientAudio.DecodedAsync(chime, DeviceApi.ToiletFormat);

        await harness.PlayAmbientAsync(toilet, "Rain");
        await harness.SetOccasionalAsync("Chime");
        // Moments while it plays, then its fade-out.
        await Task.Delay(TimeSpan.FromSeconds(2.5), Cancellation);
        await harness.EndAmbientAsync(toilet);
        var packets = new List<AudioPacket>();
        do
            packets.Add(await toilet.NextPacketAsync());
        while (!AmbientAudio.Begins(packets[^1], chimeTrack));
        packets.AddRange(await AmbientAudio.RestAsync(toilet));

        AmbientAudio.AssertNumbered(packets, first: 0);
        var start = AmbientAudio.FirstPacketOf(packets, chimeTrack);
        AmbientAudio.AssertOnePlayback(
            AmbientAudio.Samples(packets[..start]),
            await AmbientAudio.DecodedAsync(rain, DeviceApi.ToiletFormat),
            AmbientAudio.Fade,
            fadedOut: true
        );
        var sound = AmbientAudio.Samples(packets[start..]);
        Assert.Equal(chimeTrack.Length, sound.Length);
        AmbientAudio.AssertOnePlayback(sound, chimeTrack, fade: 0, fadedOut: false);
        var messages = log.Messages<OccasionalPlayer>();
        Assert.Contains(Skipped, messages);
        Assert.True(
            messages.IndexOf(Skipped) < messages.FindIndex(message => message.StartsWith("Playing occasional track Chime")),
            string.Join('\n', messages)
        );
    }

    [Theory]
    [InlineData("doorbell")]
    [InlineData("reply")]
    public async Task A_speaker_playing_another_of_the_server_s_sounds_or_a_ChatGPT_reply_is_not_picked_until_it_has_ended(string other)
    {
        using var log = ServerLog.Capture();
        await using var harness = await ServerHarness.StartAsync(await OccasionalSounds.SettingsAsync());
        var intercom = await harness.AddDeviceAsync("0x0a0001");
        await intercom.ComeOnlineAsync();
        var chime = await Tracks.WhiteNoiseAsync("wav", TimeSpan.FromSeconds(0.5), seed: 2);
        await harness.UploadAsync("Chime", chime);
        var chimeTrack = await AmbientAudio.DecodedAsync(chime, LivePlayback.Format);

        var packets = new List<AudioPacket>();
        byte[] sound;
        if (other == "doorbell")
        {
            await harness.RingDoorbellAsync();
            packets.Add(await intercom.NextPacketAsync());
            // Moments while the doorbell, 3.1 s long, plays.
            await harness.SetOccasionalAsync("Chime");
            sound = await BuiltInSounds.DecodedAsync(harness, Constants.AudioFiles.Doorbell, LivePlayback.Format);
        }
        else
        {
            await using var reply = LivePlayback.Start(harness, intercom);
            await harness.SetOccasionalAsync("Chime");
            // Moments while the reply's stream runs, before it has audio and after.
            await Task.Delay(TimeSpan.FromSeconds(1.5), Cancellation);
            reply.Append(10);
            packets.AddRange(await intercom.NextPacketsAsync(10));
            await Task.Delay(TimeSpan.FromSeconds(1), Cancellation);
            await reply.CompleteAsync();
            sound = new byte[10 * LivePlayback.ChunkBytes];
        }
        do
            packets.Add(await intercom.NextPacketAsync());
        while (!AmbientAudio.Begins(packets[^1], chimeTrack));
        packets.AddRange(await AmbientAudio.RestAsync(intercom));

        AmbientAudio.AssertNumbered(packets, first: 0);
        var (start, end) = AmbientAudio.FindSound(packets, sound, looped: false);
        Assert.Equal(0, start);
        Assert.Equal(sound.Length, packets[..end].Sum(packet => packet.Payload.Length));
        Assert.Equal(end, AmbientAudio.FirstPacketOf(packets, chimeTrack));
        AmbientAudio.AssertOnePlayback(AmbientAudio.Samples(packets[end..]), chimeTrack, fade: 0, fadedOut: false);
        Assert.Contains(Skipped, log.Messages<OccasionalPlayer>());
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("configuration")]
    [InlineData("state")]
    [InlineData("endpoint")]
    [InlineData("format")]
    public async Task A_sound_ends_once_its_device_goes_offline_is_removed_by_an_empty_configuration_or_state_or_advertises_no_endpoint_or_another_format(
        string end
    )
    {
        await using var harness = await ServerHarness.StartAsync(await OccasionalSounds.SettingsAsync());
        var intercom = await harness.AddDeviceAsync("0x0a0001");
        await intercom.ComeOnlineAsync();
        var swell = await Tracks.WhiteNoiseAsync("wav", TimeSpan.FromSeconds(2));
        await harness.UploadAsync("Swell", swell);
        var track = await AmbientAudio.DecodedAsync(swell, LivePlayback.Format);

        await harness.SetOccasionalAsync("Swell");
        var packets = await intercom.NextPacketsAsync(20);
        var otherFormat = intercom.DefaultConfiguration();
        otherFormat["audio_formats"]!["out"]!["sample_rate"] = DeviceApi.ToiletFormat.SampleRate;
        var withoutEndpoint = intercom.DefaultConfiguration();
        withoutEndpoint.Remove("endpoint");
        await (
            end switch
            {
                "offline" => intercom.PublishStateAsync(online: false),
                "configuration" => intercom.PublishConfigurationAsync(null),
                "state" => intercom.PublishAsync("state", null, retain: true),
                "endpoint" => intercom.PublishConfigurationAsync(withoutEndpoint),
                _ => intercom.PublishConfigurationAsync(otherFormat),
            }
        );
        packets.AddRange(await AmbientAudio.RestAsync(intercom));

        AmbientAudio.AssertNumbered(packets, first: 0);
        var samples = AmbientAudio.Samples(packets);
        Assert.True(samples.Length < track.Length / 2, $"The sound sent {samples.Length} of the track's {track.Length} samples.");
        AmbientAudio.AssertOnePlayback(samples, track, fade: 0, fadedOut: false);

        // A speaker again, it gets the only Occasional track again, from its start, in the format
        // it advertises.
        var format = LivePlayback.Format;
        if (end == "format")
            format = DeviceApi.ToiletFormat;
        else
            await intercom.ComeOnlineAsync();
        var again = await OccasionalSounds.NextSoundAsync(intercom);
        AmbientAudio.AssertNumbered(again, first: packets.Count);
        var full = await AmbientAudio.DecodedAsync(swell, format);
        var heard = AmbientAudio.Samples(again);
        Assert.Equal(full.Length, heard.Length);
        AmbientAudio.AssertOnePlayback(heard, full, fade: 0, fadedOut: false);
    }

    [Fact]
    public async Task The_server_s_shutdown_ends_a_sound_and_waits_for_it()
    {
        using var log = ServerLog.Capture();
        await using var harness = await ServerHarness.StartAsync(await OccasionalSounds.SettingsAsync());
        var intercom = await harness.AddDeviceAsync("0x0a0001");
        await intercom.ComeOnlineAsync();
        var swell = await Tracks.WhiteNoiseAsync("wav", TimeSpan.FromSeconds(2));
        await harness.UploadAsync("Swell", swell);
        var track = await AmbientAudio.DecodedAsync(swell, LivePlayback.Format);

        await harness.SetOccasionalAsync("Swell");
        var packets = await intercom.NextPacketsAsync(20);
        await harness.StopServerAsync();
        var stopped = Stopwatch.GetTimestamp();
        var rest = await AmbientAudio.RestAsync(intercom);

        // Nothing comes after the stop returned but what was then on its way.
        Assert.All(
            rest,
            packet => Assert.True(
                Stopwatch.GetElapsedTime(stopped, packet.ReceivedAt) < TimeSpan.FromMilliseconds(100),
                $"A packet arrived {Stopwatch.GetElapsedTime(stopped, packet.ReceivedAt).TotalMilliseconds:0} ms after the shutdown returned."
            )
        );
        packets.AddRange(rest);
        var samples = AmbientAudio.Samples(packets);
        Assert.True(samples.Length < track.Length / 2, $"The sound sent {samples.Length} of the track's {track.Length} samples.");
        AmbientAudio.AssertOnePlayback(samples, track, fade: 0, fadedOut: false);
        Assert.Contains("The server stops, so the occasional sound on device 0x0a0001 ends", log.Messages<OccasionalPlayer>());
    }
}
