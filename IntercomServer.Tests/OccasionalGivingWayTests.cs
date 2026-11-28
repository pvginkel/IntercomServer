using System.Net;
using IntercomServer.Tests.Harness;
using static IntercomServer.Tests.Harness.Commands;

namespace IntercomServer.Tests;

/// <summary>
/// "Calls and rings come first": an occasional sound gives way to what comes to its speaker, the
/// doorbell, a ring, a ChatGPT reply's stream or an ambient start, refused or not. It ends at
/// once, with nothing more sent and not to resume, what came plays as it would have, and the next
/// moment plays the round's next track from its start: the one that gave way has had its turn.
/// A call is <c>OccasionalCallTests</c>'.
/// </summary>
public sealed class OccasionalGivingWayTests
{
    private const string SpeakerId = "0x0a0001";
    private const string CallerId = "0x0a0002";
    private const string ToiletId = "0x0b0001";

    private static readonly TimeSpan TrackLength = TimeSpan.FromSeconds(1);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("doorbell")]
    [InlineData("ring")]
    [InlineData("reply")]
    public async Task A_sound_gives_way_to_the_doorbell_a_ring_or_a_ChatGPT_reply_which_plays_from_its_start_and_the_next_moment_plays_the_round_s_next_track(
        string came
    )
    {
        var settings = await OccasionalSounds.SettingsAsync();
        // So that the sound is the speaker's.
        settings["OCCASIONAL_EXCLUDED_DEVICES"] = CallerId;
        await using var harness = await ServerHarness.StartAsync(settings);
        var speaker = await harness.AddDeviceAsync(SpeakerId);
        var caller = await harness.AddDeviceAsync(CallerId);
        await speaker.ComeOnlineAsync();
        await caller.ComeOnlineAsync();
        var tracks = await harness.UploadNoiseAsync(LivePlayback.Format, TrackLength, "A", "B");

        foreach (var name in tracks.Keys)
            await harness.SetOccasionalAsync(name);
        var packets = new List<AudioPacket> { await speaker.NextPacketAsync() };
        var gaveWay = OccasionalSounds.Which(packets, tracks);
        byte[] sound;
        switch (came)
        {
            case "doorbell":
                await harness.RingDoorbellAsync();
                sound = await BuiltInSounds.DecodedAsync(harness, Constants.AudioFiles.Doorbell, LivePlayback.Format);
                break;

            case "ring":
                await Calls.RequestAsync(caller, speaker);
                sound = await BuiltInSounds.DecodedAsync(harness, Constants.AudioFiles.Ring, LivePlayback.Format);
                // Half a second of the ring, then the caller cancels.
                packets.AddRange(await AmbientAudio.UntilSoundAsync(speaker, sound));
                packets.AddRange(await speaker.NextPacketsAsync(25));
                await caller.ClickAsync();
                await caller.ExpectCommandsAsync(RedLed(Led.Off));
                await speaker.ExpectCommandsAsync(RedLed(Led.Off));
                break;

            default:
                // The stream marks the speaker from its start, before it has audio to send.
                await using (var reply = LivePlayback.Start(harness, speaker))
                {
                    await Task.Delay(TimeSpan.FromSeconds(0.5), Cancellation);
                    reply.Append(10);
                    await reply.CompleteAsync();
                }
                sound = new byte[10 * LivePlayback.ChunkBytes];
                break;
        }
        var (before, next) = await OccasionalSounds.UntilNextSoundAsync(speaker, tracks);
        packets.AddRange(before);

        AmbientAudio.AssertNumbered([.. packets, .. next], first: 0);
        var (start, end) = AmbientAudio.FindSound(packets, sound, looped: came == "ring");
        OccasionalSounds.AssertCutShort(packets[..start], tracks[gaveWay]);
        if (came != "ring")
            Assert.Equal(sound.Length, packets[start..end].Sum(packet => packet.Payload.Length));
        Assert.True(end == packets.Count, $"{packets.Count - end} packets lie between what came and the next sound.");
        var played = OccasionalSounds.Which(next, tracks);
        Assert.NotEqual(gaveWay, played);
        OccasionalSounds.AssertWhole(next, tracks[played]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_sound_gives_way_to_an_ambient_start_that_plays_as_it_would_have_or_is_refused_and_the_next_moment_plays_the_round_s_next_track(
        bool plays
    )
    {
        await using var harness = await ServerHarness.StartAsync(await OccasionalSounds.SettingsAsync());
        var toilet = await harness.AddToiletAsync(ToiletId);
        var tracks = await harness.UploadNoiseAsync(DeviceApi.ToiletFormat, TrackLength, "A", "B");
        var rain = await Tracks.WhiteNoiseAsync("wav", TrackLength, seed: 9);
        await harness.UploadAsync("Rain", rain);
        var rainTrack = await AmbientAudio.DecodedAsync(rain, DeviceApi.ToiletFormat);

        foreach (var name in tracks.Keys)
            await harness.SetOccasionalAsync(name);
        var packets = new List<AudioPacket> { await toilet.NextPacketAsync() };
        var gaveWay = OccasionalSounds.Which(packets, tracks);
        if (plays)
        {
            await harness.PlayAmbientAsync(toilet, "Rain");
            await Task.Delay(TimeSpan.FromSeconds(0.5), Cancellation);
            await harness.EndAmbientAsync(toilet);
        }
        else
        {
            // Refused once its playback is made, as it finds no such track.
            using var refused = await harness.StartAmbientAsync(harness.UserToken(), ToiletId, "Snow");
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        }
        var (before, next) = await OccasionalSounds.UntilNextSoundAsync(toilet, tracks);
        packets.AddRange(before);

        AmbientAudio.AssertNumbered([.. packets, .. next], first: 0);
        var start = plays ? AmbientAudio.FirstPacketOf(packets, rainTrack) : packets.Count;
        OccasionalSounds.AssertCutShort(packets[..start], tracks[gaveWay]);
        if (plays)
            AmbientAudio.AssertOnePlayback(AmbientAudio.Samples(packets[start..]), rainTrack, AmbientAudio.Fade, fadedOut: true);
        var played = OccasionalSounds.Which(next, tracks);
        Assert.NotEqual(gaveWay, played);
        OccasionalSounds.AssertWhole(next, tracks[played]);
    }

    [Fact]
    public async Task A_sound_that_gave_way_has_had_its_turn_so_each_round_plays_every_Occasional_track_once_though_every_sound_gives_way()
    {
        await using var harness = await ServerHarness.StartAsync(await OccasionalSounds.SettingsAsync());
        var speaker = await harness.AddDeviceAsync(SpeakerId);
        await speaker.ComeOnlineAsync();
        var tracks = await harness.UploadNoiseAsync(LivePlayback.Format, TimeSpan.FromSeconds(0.5), "A", "B", "C");

        foreach (var name in tracks.Keys)
            await harness.SetOccasionalAsync(name);
        // Four rounds, each sound giving way to a reply's stream that sends nothing in its fifth of
        // a second. A turn given back would leave its track due beside the round's others, so a
        // round's last would be one it had played one time in two.
        var played = new List<string>();
        for (var i = 0; i < 4 * tracks.Count; i++)
        {
            var sound = new List<AudioPacket> { await speaker.NextPacketAsync() };
            await using (var reply = LivePlayback.Start(harness, speaker))
            {
                await Task.Delay(TimeSpan.FromSeconds(0.2), Cancellation);
                await reply.CompleteAsync();
            }
            sound.AddRange(speaker.TakeArrivedPackets());
            played.Add(OccasionalSounds.Which(sound, tracks));
            OccasionalSounds.AssertCutShort(sound, tracks[played[^1]]);
        }

        Assert.All(played.Chunk(tracks.Count), round => Assert.Equal(tracks.Keys.Order(), round.Order()));
    }
}
