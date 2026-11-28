using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using IntercomServer.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace IntercomServer.Tests;

/// <summary>
/// The ambient player, started and stopped through the device routes, heard at the device's UDP
/// listener. A playback is one track, decoded once by the real FFmpeg into the device's format:
/// at the loudness target, its peak held below −1 dBFS, faded in over half a second, looped
/// without a gap on the device's counter, and faded out over half a second at its stop, after
/// which nothing more is sent: a stop by the routes, the device going offline or being removed,
/// or the server's shutdown, which waits for every fade-out. It plays its own copy of the track, follows the endpoint the device
/// advertises, and is the device's only one. A start without a track picks an enabled one at
/// random, never the one the device last played unless it is the only one, and nothing of a
/// playback is written anywhere.
/// </summary>
public sealed class AmbientPlayerTests
{
    private static readonly TimeSpan TrackLength = TimeSpan.FromSeconds(1);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_playback_is_the_decoded_track_at_the_loudness_target_faded_in_looped_without_a_gap_and_faded_out_at_its_stop()
    {
        await using var harness = await ServerHarness.StartAsync();
        var toilet = await harness.AddToiletAsync("0x0b0001");
        var noise = await Tracks.WhiteNoiseAsync("wav", TrackLength);
        await harness.UploadAsync("Noise", noise);
        var track = await AmbientAudio.DecodedAsync(noise, DeviceApi.ToiletFormat);
        var measured = await AmbientAudio.MeasuredAsync(noise, DeviceApi.ToiletFormat);

        Assert.Equal("Noise", await harness.PlayAmbientAsync(toilet));
        // Past the fade-in and two of the loop's seams.
        var packets = await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(2.2)));
        await harness.EndAmbientAsync(toilet);
        packets.AddRange(await AmbientAudio.RestAsync(toilet));

        AmbientAudio.AssertNumbered(packets, first: 0);
        // Each 20 ms chunk at 48 kHz goes as two equal packets, the loop's seam and the fade-out's
        // end among them.
        Assert.All(packets, packet => Assert.Equal(960, packet.Payload.Length));
        var samples = AmbientAudio.Samples(packets);
        var gain = AmbientAudio.AssertOnePlayback(samples, track, AmbientAudio.Fade, fadedOut: true);
        // The loudness target, −20 LUFS, less the track's loudness: its peak, about −12 dBFS, stays
        // well below −1 dBFS at that gain.
        Assert.InRange(gain, -20 - measured.Loudness - 0.01, -20 - measured.Loudness + 0.01);
        var heard = await AmbientAudio.MeasuredAsync(
            Tracks.Wav(samples[AmbientAudio.Fade..^AmbientAudio.Fade], DeviceApi.ToiletFormat),
            DeviceApi.ToiletFormat
        );
        Assert.InRange(heard.Loudness, -20.3, -19.7);
    }

    [Fact]
    public async Task The_gain_is_held_down_so_the_peak_stays_below_minus_1_dBFS()
    {
        // The noise measures about −14 LUFS, so a target of −1 LUFS would lift its peak of about
        // −12 dBFS past 0 dBFS.
        await using var harness = await ServerHarness.StartAsync(
            new Dictionary<string, string?> { ["AMBIENT_LOUDNESS_LUFS"] = "-1" }
        );
        var toilet = await harness.AddToiletAsync("0x0b0001");
        var noise = await Tracks.WhiteNoiseAsync("wav", TrackLength);
        await harness.UploadAsync("Noise", noise);
        var track = await AmbientAudio.DecodedAsync(noise, DeviceApi.ToiletFormat);
        var measured = await AmbientAudio.MeasuredAsync(noise, DeviceApi.ToiletFormat);

        await harness.PlayAmbientAsync(toilet);
        var packets = await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(1)));
        await harness.EndAmbientAsync(toilet);
        packets.AddRange(await AmbientAudio.RestAsync(toilet));

        var samples = AmbientAudio.Samples(packets);
        var gain = AmbientAudio.AssertOnePlayback(samples, track, AmbientAudio.Fade, fadedOut: true);
        Assert.True(gain < -1 - measured.Loudness - 1, $"The gain, {gain:0.00} dB, was not held down.");
        // FFmpeg's meter gives the peak to a tenth of a dB.
        Assert.InRange(gain, -1 - measured.Peak - 0.1, -1 - measured.Peak);
        Assert.InRange(AmbientAudio.Peak(samples), -1.2, -1.0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(200)]
    public async Task The_fades_last_AMBIENT_FADE_MS_and_zero_plays_and_stops_at_full_level(int milliseconds)
    {
        await using var harness = await ServerHarness.StartAsync(
            new Dictionary<string, string?> { ["AMBIENT_FADE_MS"] = milliseconds.ToString(CultureInfo.InvariantCulture) }
        );
        var toilet = await harness.AddToiletAsync("0x0b0001");
        var rain = await Tracks.WhiteNoiseAsync("wav", TrackLength);
        await harness.UploadAsync("Rain", rain);

        await harness.PlayAmbientAsync(toilet);
        var packets = await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(0.6)));
        await harness.EndAmbientAsync(toilet);
        packets.AddRange(await AmbientAudio.RestAsync(toilet));

        AmbientAudio.AssertOnePlayback(
            AmbientAudio.Samples(packets),
            await AmbientAudio.DecodedAsync(rain, DeviceApi.ToiletFormat),
            DeviceApi.ToiletFormat.SampleRate * milliseconds / 1000,
            fadedOut: true
        );
    }

    [Fact]
    public async Task A_start_naming_a_track_plays_it_enabled_or_disabled_and_one_without_a_track_with_none_enabled_answers_409()
    {
        await using var harness = await ServerHarness.StartAsync();
        var toilet = await harness.AddToiletAsync("0x0b0001");
        var token = harness.UserToken();

        using (var none = await harness.StartAmbientAsync(token, toilet.DeviceId))
        {
            Assert.Equal(HttpStatusCode.Conflict, none.StatusCode);
            Assert.Equal("There is no enabled track to play.", await TrackApi.ReadProblemDetailAsync(none));
        }

        var rain = await Tracks.WhiteNoiseAsync("wav", TrackLength, seed: 1);
        var snow = await Tracks.WhiteNoiseAsync("wav", TrackLength, seed: 2);
        await harness.UploadAsync("Rain", rain);
        await harness.UploadAsync("Snow", snow);
        await harness.TrackStore().UpdateAsync("Rain", null, false, Cancellation);
        await harness.TrackStore().UpdateAsync("Snow", null, false, Cancellation);

        using (var disabled = await harness.StartAmbientAsync(token, toilet.DeviceId))
        {
            Assert.Equal(HttpStatusCode.Conflict, disabled.StatusCode);
            Assert.Equal("There is no enabled track to play.", await TrackApi.ReadProblemDetailAsync(disabled));
        }

        // A name is looked up ignoring case.
        Assert.Equal("Snow", await harness.PlayAmbientAsync(toilet, "snow"));
        var packets = await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(0.7)));
        await harness.EndAmbientAsync(toilet);
        packets.AddRange(await AmbientAudio.RestAsync(toilet));

        AmbientAudio.AssertOnePlayback(
            AmbientAudio.Samples(packets),
            await AmbientAudio.DecodedAsync(snow, DeviceApi.ToiletFormat),
            AmbientAudio.Fade,
            fadedOut: true
        );
    }

    [Fact]
    public async Task A_start_without_a_track_picks_an_enabled_one_at_random_never_the_one_the_device_last_played_and_writes_nothing()
    {
        await using var harness = await ServerHarness.StartAsync(
            new Dictionary<string, string?> { ["AMBIENT_FADE_MS"] = "0" }
        );
        var toilet = await harness.AddToiletAsync("0x0b0001");
        var recording = await Tracks.WhiteNoiseAsync("wav", TimeSpan.FromSeconds(0.2));
        foreach (var name in new[] { "A", "B", "C", "D" })
            await harness.UploadAsync(name, recording);
        await harness.TrackStore().UpdateAsync("D", null, false, Cancellation);
        var stored = DataVolume(harness);

        var picked = new List<string>();
        for (var start = 0; start < 20; start++)
        {
            picked.Add(await harness.PlayAmbientAsync(toilet));
            await harness.EndAmbientAsync(toilet);
        }

        // A track that each pick at random among two would miss nineteen times running.
        Assert.Equal(["A", "B", "C"], picked.Distinct().Order(StringComparer.Ordinal));
        Assert.All(picked.Zip(picked.Skip(1)), pair => Assert.NotEqual(pair.First, pair.Second));
        Assert.Equal(stored, DataVolume(harness));
        Assert.Null((await harness.AwaitDeviceAsync(toilet.DeviceId, _ => true)).Track);

        // The one enabled track is picked every time, though the device last played it.
        await harness.TrackStore().UpdateAsync("B", null, false, Cancellation);
        await harness.TrackStore().UpdateAsync("C", null, false, Cancellation);
        for (var start = 0; start < 3; start++)
        {
            Assert.Equal("A", await harness.PlayAmbientAsync(toilet));
            await harness.EndAmbientAsync(toilet);
        }
    }

    [Fact]
    public async Task A_start_on_a_device_already_playing_changes_nothing()
    {
        await using var harness = await ServerHarness.StartAsync();
        var toilet = await harness.AddToiletAsync("0x0b0001");
        var rain = await Tracks.WhiteNoiseAsync("wav", TrackLength, seed: 1);
        await harness.UploadAsync("Rain", rain);
        await harness.UploadAsync("Snow", await Tracks.WhiteNoiseAsync("wav", TrackLength, seed: 2));

        Assert.Equal("Rain", await harness.PlayAmbientAsync(toilet, "Rain"));
        var packets = await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(0.6)));
        Assert.Equal("Rain", await harness.PlayAmbientAsync(toilet, "Snow"));
        Assert.Equal("Rain", await harness.PlayAmbientAsync(toilet));
        packets.AddRange(await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(0.6))));
        await harness.EndAmbientAsync(toilet);
        packets.AddRange(await AmbientAudio.RestAsync(toilet));

        // Had a start restarted the playback, or played a second one beside it, its audio would
        // not be one playback of the track.
        AmbientAudio.AssertNumbered(packets, first: 0);
        AmbientAudio.AssertOnePlayback(
            AmbientAudio.Samples(packets),
            await AmbientAudio.DecodedAsync(rain, DeviceApi.ToiletFormat),
            AmbientAudio.Fade,
            fadedOut: true
        );
    }

    [Fact]
    public async Task Two_starts_that_race_make_one_playback()
    {
        await using var harness = await ServerHarness.StartAsync();
        var toilet = await harness.AddToiletAsync("0x0b0001");
        var recordings = new Dictionary<string, byte[]>
        {
            ["Rain"] = await Tracks.WhiteNoiseAsync("wav", TrackLength, seed: 1),
            ["Snow"] = await Tracks.WhiteNoiseAsync("wav", TrackLength, seed: 2),
        };
        foreach (var (name, recording) in recordings)
            await harness.UploadAsync(name, recording);

        var started = await Task.WhenAll(harness.PlayAmbientAsync(toilet), harness.PlayAmbientAsync(toilet));
        Assert.Equal(started[0], started[1]);
        var packets = await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(0.7)));
        await harness.EndAmbientAsync(toilet);
        packets.AddRange(await AmbientAudio.RestAsync(toilet));

        // A second playback would have sent beside the first, and played on past the stop.
        AmbientAudio.AssertOnePlayback(
            AmbientAudio.Samples(packets),
            await AmbientAudio.DecodedAsync(recordings[started[0]], DeviceApi.ToiletFormat),
            AmbientAudio.Fade,
            fadedOut: true
        );
    }

    [Fact]
    public async Task A_start_during_a_fade_out_plays_once_the_fade_out_has_ended_though_a_start_between_them_was_refused()
    {
        // A fade that outlasts the refused start and the next one's decode.
        await using var harness = await ServerHarness.StartAsync(
            new Dictionary<string, string?> { ["AMBIENT_FADE_MS"] = "1000" }
        );
        const int fade = 48000;
        var toilet = await harness.AddToiletAsync("0x0b0001");
        var rain = await Tracks.WhiteNoiseAsync("wav", TrackLength, seed: 1);
        var snow = await Tracks.WhiteNoiseAsync("wav", TrackLength, seed: 2);
        await harness.UploadAsync("Rain", rain);
        await harness.UploadAsync("Snow", snow);
        var token = harness.UserToken();

        await harness.PlayAmbientAsync(toilet, "Rain");
        var packets = await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(1.2)));
        var stopping = harness.StopAmbientAsync(token, toilet.DeviceId);
        // Until the stop has reached the player, a start joins the playback it stops.
        while (true)
        {
            using var refused = await harness.StartAmbientAsync(token, toilet.DeviceId, "Nope");
            if (refused.StatusCode == HttpStatusCode.NotFound)
                break;
            Assert.Equal(HttpStatusCode.OK, refused.StatusCode);
        }
        Assert.Equal("Rain", (await harness.AwaitDeviceAsync(toilet.DeviceId, _ => true)).Track);

        Assert.Equal("Snow", await harness.PlayAmbientAsync(toilet, "Snow"));
        using (var stopped = await stopping)
            Assert.Equal(HttpStatusCode.NoContent, stopped.StatusCode);
        // Past the second sound's fade-in.
        packets.AddRange(toilet.TakeArrivedPackets());
        packets.AddRange(await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(1.2))));
        await harness.EndAmbientAsync(toilet);
        packets.AddRange(await AmbientAudio.RestAsync(toilet));

        // Sounds that overlapped would have their packets interleaved on the device's counter.
        AmbientAudio.AssertNumbered(packets, first: 0);
        var snowTrack = await AmbientAudio.DecodedAsync(snow, DeviceApi.ToiletFormat);
        var second = AmbientAudio.FirstPacketOf(packets, snowTrack);
        AmbientAudio.AssertOnePlayback(
            AmbientAudio.Samples(packets.Take(second)),
            await AmbientAudio.DecodedAsync(rain, DeviceApi.ToiletFormat),
            fade,
            fadedOut: true
        );
        AmbientAudio.AssertOnePlayback(AmbientAudio.Samples(packets.Skip(second)), snowTrack, fade, fadedOut: true);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_stop_during_the_decode_abandons_the_start_and_ends_its_FFmpeg()
    {
        using var ffmpeg = new HeldFfmpeg();
        await using var harness = await ServerHarness.StartAsync(
            services: services => services.AddSingleton(new AudioDecoder(ffmpeg.Executable))
        );
        var toilet = await harness.AddToiletAsync("0x0b0001");
        var token = harness.UserToken();
        // Small enough for FFmpeg's input pipe to take whole while FFmpeg is held.
        await harness.UploadAsync("Rain", await Tracks.WhiteNoiseAsync("mp3", TrackLength));

        ffmpeg.Hold();
        var starting = harness.StartAmbientAsync(token, toilet.DeviceId);
        await ffmpeg.HeldAsync();
        Assert.Single(ffmpeg.Processes());
        using (var stop = await harness.StopAmbientAsync(token, toilet.DeviceId))
            Assert.Equal(HttpStatusCode.NoContent, stop.StatusCode);

        using (var start = await starting)
        {
            Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
            Assert.Equal("The start was stopped before its sound began.", await TrackApi.ReadProblemDetailAsync(start));
        }
        Assert.Empty(ffmpeg.Processes());

        // The device's first packet is its counter's first: the abandoned start sent nothing.
        ffmpeg.Release();
        await harness.PlayAmbientAsync(toilet);
        Assert.Equal(0, (await toilet.NextPacketAsync()).Index);
    }

    [Fact]
    public async Task A_playing_track_plays_on_unchanged_when_it_is_renamed_disabled_or_deleted()
    {
        await using var harness = await ServerHarness.StartAsync();
        var toilet = await harness.AddToiletAsync("0x0b0001");
        var rain = await Tracks.WhiteNoiseAsync("wav", TrackLength);
        await harness.UploadAsync("Rain", rain);
        var store = harness.TrackStore();

        await harness.PlayAmbientAsync(toilet);
        var packets = await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(0.6)));
        Assert.NotNull((await store.UpdateAsync("Rain", "Drizzle", null, Cancellation)).Track);
        packets.AddRange(await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(0.2))));
        Assert.NotNull((await store.UpdateAsync("Drizzle", null, false, Cancellation)).Track);
        packets.AddRange(await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(0.2))));
        Assert.NotNull((await store.DeleteAsync("Drizzle", Cancellation)).Track);
        // On past the loop's seam, where a playback that read the file again would find it gone.
        packets.AddRange(await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(0.4))));
        Assert.Equal("Rain", (await harness.AwaitDeviceAsync(toilet.DeviceId, _ => true)).Track);
        await harness.EndAmbientAsync(toilet);
        packets.AddRange(await AmbientAudio.RestAsync(toilet));

        AmbientAudio.AssertOnePlayback(
            AmbientAudio.Samples(packets),
            await AmbientAudio.DecodedAsync(rain, DeviceApi.ToiletFormat),
            AmbientAudio.Fade,
            fadedOut: true
        );
    }

    [Fact]
    public async Task A_device_that_advertises_a_new_endpoint_gets_the_next_packet_there_and_one_that_advertises_none_ends_its_playback()
    {
        await using var harness = await ServerHarness.StartAsync();
        var toilet = await harness.AddToiletAsync("0x0b0001");
        // Only its UDP listener: the toilet's new endpoint.
        var elsewhere = await harness.AddDeviceAsync("0x0b0002", DeviceApi.ToiletPrefix);
        var rain = await Tracks.WhiteNoiseAsync("wav", TrackLength);
        await harness.UploadAsync("Rain", rain);

        await harness.PlayAmbientAsync(toilet);
        var packets = await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(0.6)));
        var configuration = DeviceApi.ToiletConfiguration(toilet);
        configuration["endpoint"] = elsewhere.Endpoint;
        await toilet.PublishConfigurationAsync(configuration);

        var moved = await elsewhere.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(0.6)));
        packets.AddRange(toilet.TakeArrivedPackets());
        configuration.Remove("endpoint");
        await toilet.PublishConfigurationAsync(configuration);
        moved.AddRange(await AmbientAudio.RestAsync(elsewhere));

        Assert.Equal(0, toilet.ReceivedPacketCount);
        Assert.Null((await harness.AwaitDeviceAsync(toilet.DeviceId, device => device.Track == null)).Track);
        // The counter runs on at the new address, and so does the track.
        AmbientAudio.AssertNumbered([.. packets, .. moved], first: 0);
        AmbientAudio.AssertOnePlayback(
            AmbientAudio.Samples([.. packets, .. moved]),
            await AmbientAudio.DecodedAsync(rain, DeviceApi.ToiletFormat),
            AmbientAudio.Fade,
            fadedOut: false
        );
    }

    [Fact]
    public async Task Each_device_has_a_playback_of_its_own()
    {
        await using var harness = await ServerHarness.StartAsync();
        var stopped = await harness.AddToiletAsync("0x0b0001");
        var playing = await harness.AddToiletAsync("0x0b0002");
        await harness.UploadAsync("Rain", await Tracks.WhiteNoiseAsync("wav", TrackLength, seed: 1));
        await harness.UploadAsync("Snow", await Tracks.WhiteNoiseAsync("wav", TrackLength, seed: 2));

        await harness.PlayAmbientAsync(stopped, "Rain");
        await harness.PlayAmbientAsync(playing, "Snow");
        await stopped.NextPacketsAsync(10);
        await playing.NextPacketsAsync(10);
        await harness.EndAmbientAsync(stopped);
        await AmbientAudio.RestAsync(stopped);

        // The other plays on past the stop, which the stopped one took half a second of quiet to see.
        playing.TakeArrivedPackets();
        Assert.NotNull(await playing.NextPacketWithinAsync(AmbientAudio.Quiet));
        Assert.Null((await harness.AwaitDeviceAsync(stopped.DeviceId, _ => true)).Track);
        Assert.Equal("Snow", (await harness.AwaitDeviceAsync(playing.DeviceId, _ => true)).Track);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("configuration")]
    [InlineData("state")]
    public async Task A_device_that_goes_offline_or_is_removed_by_an_empty_configuration_or_state_has_its_playback_faded_out(
        string end
    )
    {
        await using var harness = await ServerHarness.StartAsync();
        var toilet = await harness.AddToiletAsync("0x0b0001");
        var rain = await Tracks.WhiteNoiseAsync("wav", TrackLength);
        await harness.UploadAsync("Rain", rain);
        var track = await AmbientAudio.DecodedAsync(rain, DeviceApi.ToiletFormat);

        await harness.PlayAmbientAsync(toilet);
        var packets = await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(0.6)));
        await (
            end switch
            {
                "offline" => toilet.PublishStateAsync(online: false),
                "configuration" => toilet.PublishConfigurationAsync(null),
                _ => toilet.PublishAsync("state", null, retain: true),
            }
        );
        packets.AddRange(await AmbientAudio.RestAsync(toilet));

        AmbientAudio.AssertNumbered(packets, first: 0);
        AmbientAudio.AssertOnePlayback(AmbientAudio.Samples(packets), track, AmbientAudio.Fade, fadedOut: true);

        // Back, the device gets a playback of its own, from its fade-in: a start that found the
        // stopped one would join it.
        await toilet.PublishConfigurationAsync(DeviceApi.ToiletConfiguration(toilet));
        await toilet.PublishStateAsync();
        Assert.Null((await harness.AwaitDeviceAsync(toilet.DeviceId, device => device.Online)).Track);
        await harness.PlayAmbientAsync(toilet);
        var again = await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(0.6)));
        await harness.EndAmbientAsync(toilet);
        again.AddRange(await AmbientAudio.RestAsync(toilet));

        AmbientAudio.AssertNumbered(again, first: packets.Count);
        AmbientAudio.AssertOnePlayback(AmbientAudio.Samples(again), track, AmbientAudio.Fade, fadedOut: true);
    }

    [Fact]
    public async Task The_server_s_shutdown_fades_out_every_playback_and_waits_for_it()
    {
        await using var harness = await ServerHarness.StartAsync();
        TestDevice[] toilets = [await harness.AddToiletAsync("0x0b0001"), await harness.AddToiletAsync("0x0b0002")];
        var rain = await Tracks.WhiteNoiseAsync("wav", TrackLength);
        await harness.UploadAsync("Rain", rain);
        var track = await AmbientAudio.DecodedAsync(rain, DeviceApi.ToiletFormat);

        var packets = new Dictionary<TestDevice, List<AudioPacket>>();
        foreach (var toilet in toilets)
            await harness.PlayAmbientAsync(toilet);
        foreach (var toilet in toilets)
            packets[toilet] = await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(0.6)));
        await harness.StopServerAsync();
        var stopped = Stopwatch.GetTimestamp();

        foreach (var toilet in toilets)
        {
            var rest = await AmbientAudio.RestAsync(toilet);
            // The fade-out takes half a second to go out: nothing comes after the stop returned
            // but what was then on its way.
            Assert.All(
                rest,
                packet => Assert.True(
                    Stopwatch.GetElapsedTime(stopped, packet.ReceivedAt) < TimeSpan.FromMilliseconds(100),
                    $"A packet to device {toilet.DeviceId} arrived {Stopwatch.GetElapsedTime(stopped, packet.ReceivedAt).TotalMilliseconds:0} ms after the shutdown returned."
                )
            );
            packets[toilet].AddRange(rest);

            AmbientAudio.AssertNumbered(packets[toilet], first: 0);
            AmbientAudio.AssertOnePlayback(AmbientAudio.Samples(packets[toilet]), track, AmbientAudio.Fade, fadedOut: true);
        }
    }

    // Every file under the data volume, with its length and its last write.
    private static List<string> DataVolume(ServerHarness harness) =>
        Directory
            .EnumerateFileSystemEntries(harness.DataDirectory, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(path =>
                File.Exists(path) ? $"{path} {new FileInfo(path).Length} {File.GetLastWriteTimeUtc(path):O}" : path
            )
            .ToList();
}
