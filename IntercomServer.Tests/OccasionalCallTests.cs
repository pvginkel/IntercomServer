using System.Diagnostics;
using IntercomServer.Tests.Harness;
using static IntercomServer.Tests.Harness.Commands;

namespace IntercomServer.Tests;

/// <summary>
/// "Calls and rings come first", for the call: a speaker that is calling, ringing or in a call is
/// busy, marked by no sound of the server's while it calls, while its call waits once the doorbell
/// has replaced its ring, and in the call, whose audio goes from device to device. It is not
/// picked, and an occasional sound on it gives way once it calls, joins a call or is put straight
/// into one: the sound ends, not to resume, the call goes as it would have, and the next moment
/// plays the round's next track from its start.
/// </summary>
public sealed class OccasionalCallTests
{
    private const string Skipped = "No speaker is idle, so this moment plays no occasional sound";

    private const string SpeakerId = "0x0a0001";
    private const string FirstId = "0x0a0002";
    private const string SecondId = "0x0a0003";

    private static readonly TimeSpan TrackLength = TimeSpan.FromSeconds(1);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_speaker_that_is_calling_ringing_or_in_a_call_is_not_picked_and_the_moments_are_skipped_until_the_call_is_over()
    {
        using var log = ServerLog.Capture();
        await using var harness = await ServerHarness.StartAsync(await OccasionalSounds.SettingsAsync());
        var caller = await harness.AddDeviceAsync(FirstId);
        var called = await harness.AddDeviceAsync(SecondId);
        await caller.ComeOnlineAsync();
        await called.ComeOnlineAsync();
        var chime = await Tracks.WhiteNoiseAsync("wav", TimeSpan.FromSeconds(0.3));
        await harness.UploadAsync("Chime", chime);
        var chimeTrack = await AmbientAudio.DecodedAsync(chime, LivePlayback.Format);

        await Calls.RequestAsync(caller, called);
        await harness.SetOccasionalAsync("Chime");
        // Moments while the caller calls, to which no sound of the server's plays, and the ring
        // plays to the called device.
        await Task.Delay(TimeSpan.FromSeconds(2), Cancellation);
        Assert.Equal(0, caller.ReceivedPacketCount);

        // The doorbell replaces the ring, and the call still waits, the called device rung with no
        // sound of the server's playing to it once the doorbell has ended.
        await harness.RingDoorbellAsync();
        var heard = await caller.NextSoundAsync(BuiltInSounds.DoorbellLength + BuiltInSounds.Margin);
        heard.AddRange(called.TakeArrivedPackets());
        Assert.DoesNotContain(heard, packet => AmbientAudio.Begins(packet, chimeTrack));
        await Task.Delay(TimeSpan.FromSeconds(2), Cancellation);
        Assert.Equal(0, caller.ReceivedPacketCount);
        Assert.Equal(0, called.ReceivedPacketCount);

        await called.ClickAsync();
        await caller.ExpectCommandsAsync(RedLed(Led.Off), GreenLed(Led.On), Recording(true), AddEndpoint(called.Endpoint));
        await called.ExpectCommandsAsync(RedLed(Led.Off), AddEndpoint(caller.Endpoint), GreenLed(Led.On), Recording(true));
        // Moments during the call.
        await Task.Delay(TimeSpan.FromSeconds(2), Cancellation);
        Assert.Equal(0, caller.ReceivedPacketCount);
        Assert.Equal(0, called.ReceivedPacketCount);
        Assert.Contains(Skipped, log.Messages<OccasionalPlayer>());
        Assert.All(log.Messages<OccasionalPlayer>(), message => Assert.Equal(Skipped, message));

        // Over, the call leaves them idle, and a moment plays on one of them.
        await Calls.EndAsync(caller, caller, called);
        var waited = Stopwatch.StartNew();
        while (caller.ReceivedPacketCount + called.ReceivedPacketCount == 0)
        {
            Assert.True(waited.Elapsed < Timeouts.Observe, "No sound played once the call was over.");
            await Task.Delay(TimeSpan.FromMilliseconds(10), Cancellation);
        }
    }

    [Theory]
    [InlineData("calls")]
    [InlineData("joins")]
    [InlineData("is put straight into one")]
    public async Task A_sound_gives_way_once_its_speaker_calls_joins_a_call_or_is_put_straight_into_one_and_the_next_moment_plays_the_round_s_next_track(
        string call
    )
    {
        var settings = await OccasionalSounds.SettingsAsync();
        // So that the sound is the speaker's.
        settings["OCCASIONAL_EXCLUDED_DEVICES"] = $"{FirstId},{SecondId}";
        await using var harness = await ServerHarness.StartAsync(settings);
        var speaker = await harness.AddDeviceAsync(SpeakerId);
        var first = await harness.AddDeviceAsync(FirstId);
        var second = await harness.AddDeviceAsync(SecondId);
        await first.ComeOnlineAsync();
        if (call == "joins")
        {
            // A call the speaker, not yet online, is not rung for.
            await second.ComeOnlineAsync();
            await Calls.StartAsync(first, second);
        }
        await speaker.ComeOnlineAsync();
        if (call == "is put straight into one")
            await harness.PublishAsync("intercom/server/set/auto_accept", "true");
        var tracks = await harness.UploadNoiseAsync(LivePlayback.Format, TrackLength, "A", "B");

        foreach (var name in tracks.Keys)
            await harness.SetOccasionalAsync(name);
        var packets = new List<AudioPacket> { await speaker.NextPacketAsync() };
        var gaveWay = OccasionalSounds.Which(packets, tracks);
        // Each call lasts a second, longer than the rest of the sound would have played.
        switch (call)
        {
            case "calls":
                await Calls.RequestAsync(speaker, first);
                await Task.Delay(TimeSpan.FromSeconds(1), Cancellation);
                await speaker.ClickAsync();
                await speaker.ExpectCommandsAsync(RedLed(Led.Off));
                await first.ExpectCommandsAsync(RedLed(Led.Off));
                break;

            case "joins":
                await Calls.JoinAsync(speaker, first, second);
                await Task.Delay(TimeSpan.FromSeconds(1), Cancellation);
                await Calls.EndAsync(speaker, first, second, speaker);
                break;

            default:
                await first.ClickAsync();
                await first.ExpectCommandsAsync(
                    RedLed(Led.Off),
                    GreenLed(Led.On),
                    Recording(true),
                    AddEndpoint(speaker.Endpoint)
                );
                await speaker.ExpectCommandsAsync(
                    RedLed(Led.Off),
                    AddEndpoint(first.Endpoint),
                    GreenLed(Led.On),
                    Recording(true)
                );
                await Task.Delay(TimeSpan.FromSeconds(1), Cancellation);
                await Calls.EndAsync(first, first, speaker);
                break;
        }
        var (before, next) = await OccasionalSounds.UntilNextSoundAsync(speaker, tracks);
        packets.AddRange(before);

        AmbientAudio.AssertNumbered([.. packets, .. next], first: 0);
        OccasionalSounds.AssertCutShort(packets, tracks[gaveWay]);
        var played = OccasionalSounds.Which(next, tracks);
        Assert.NotEqual(gaveWay, played);
        OccasionalSounds.AssertWhole(next, tracks[played]);
    }
}
