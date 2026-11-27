using System.Diagnostics;
using IntercomServer.Tests.Harness;

namespace IntercomServer.Tests;

/// <summary>
/// How a sound plays: a looped one, the ring a call request plays to the devices it rings,
/// goes on until it is cancelled. Each device has a playback of its own, so a new sound
/// replaces what the devices it reaches were playing, and nothing else. A device the server
/// cannot send to is skipped, and the others play.
/// </summary>
public sealed class PlaybackTests
{
    // A fifth of a second of the doorbell, one packet each 20 ms.
    private const int Packets = 10;

    // The doorbell's first packets to a ringing device may race the ring's last, so a tenth of a
    // second of them is left out of a comparison.
    private const int RaceAllowance = 5;

    [Fact]
    public async Task A_looped_sound_plays_on_past_its_end_until_it_is_cancelled()
    {
        await using var harness = await ServerHarness.StartAsync();
        var caller = await harness.AddDeviceAsync("0x0a0001");
        var called = await harness.AddDeviceAsync("0x0a0002");
        await caller.ComeOnlineAsync();
        await called.ComeOnlineAsync();

        await caller.ClickAsync();

        var ringing = BuiltInSounds.Bytes(BuiltInSounds.RingLength + BuiltInSounds.Margin);
        for (var received = 0; received < ringing; )
            received += (await called.NextPacketAsync()).Payload.Length;

        await caller.ClickAsync();

        (await called.NextCommandAsync()).AssertIs("set/red_led", """{"state":"blink","on":500,"off":500}""");
        (await called.NextCommandAsync()).AssertIs("set/red_led", """{"state":"off"}""");
        var cancelled = Stopwatch.GetTimestamp();

        // The server cancels the ring right after it turns the LEDs off. Had it played on,
        // its packets would still be arriving a second later.
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        var last = called.TakeArrivedPackets().LastOrDefault();
        Assert.True(
            last == null || Stopwatch.GetElapsedTime(cancelled, last.ReceivedAt) < TimeSpan.FromSeconds(0.5),
            "The ring played on after the call was cancelled."
        );
    }

    [Fact]
    public async Task A_new_sound_replaces_what_the_devices_it_reaches_were_playing_and_nothing_else()
    {
        await using var harness = await ServerHarness.StartAsync();
        var caller = await harness.AddDeviceAsync("0x0a0001");
        var reached = await harness.AddDeviceAsync("0x0a0002");
        var passedOver = await harness.AddDeviceAsync("0x0a0003");
        await caller.ComeOnlineAsync();
        await reached.ComeOnlineAsync();
        await passedOver.ComeOnlineAsync();

        await caller.ClickAsync();
        await reached.NextPacketsAsync(5);
        await passedOver.NextPacketsAsync(5);
        // Disabled, it rings on, and the doorbell does not reach it.
        await passedOver.PublishStateAsync(enabled: false);

        await harness.RingDoorbellAsync();

        var doorbell = await caller.NextSoundAsync(BuiltInSounds.DoorbellLength + BuiltInSounds.Margin);

        // The device the doorbell reached ends on it: had the ring played on beside it, or after
        // it, ring packets would sit among or after the doorbell's.
        var toReached = reached.TakeArrivedPackets();
        Assert.True(
            AudioPacket.SameAudio(
                doorbell.Skip(RaceAllowance),
                toReached.TakeLast(doorbell.Count - RaceAllowance)
            ),
            $"The reached device's last {doorbell.Count - RaceAllowance} of {toReached.Count} packets are not the doorbell's."
        );

        // The device it passed over rings on past the doorbell's end, which the caller took a
        // second without packets to see.
        var toPassedOver = passedOver.TakeArrivedPackets();
        Assert.True(
            toPassedOver.Count > 0 && toPassedOver[^1].ArrivedAfter(doorbell[^1]) > TimeSpan.FromSeconds(0.5),
            "The ring stopped on the device the doorbell did not reach."
        );
    }

    [Fact]
    public async Task A_device_without_a_configuration_is_skipped_and_the_others_play()
    {
        await using var harness = await ServerHarness.StartAsync();
        var unconfigured = await harness.AddDeviceAsync("0x0a0001");
        var configured = await harness.AddDeviceAsync("0x0a0002");
        await unconfigured.PublishStateAsync();
        await configured.ComeOnlineAsync();

        await AssertSkippedWhileTheOthersPlay(harness, configured, unconfigured);
    }

    [Fact]
    public async Task A_device_whose_configuration_has_no_endpoint_is_skipped_and_the_others_play()
    {
        await using var harness = await ServerHarness.StartAsync();
        var noEndpoint = await harness.AddDeviceAsync("0x0a0001");
        var configured = await harness.AddDeviceAsync("0x0a0002");
        var configuration = noEndpoint.DefaultConfiguration();
        configuration.Remove("endpoint");
        await noEndpoint.PublishConfigurationAsync(configuration);
        await noEndpoint.PublishStateAsync();
        await configured.ComeOnlineAsync();

        await AssertSkippedWhileTheOthersPlay(harness, configured, noEndpoint);
    }

    [Fact]
    public async Task A_device_whose_endpoint_is_no_address_is_skipped_and_the_others_play()
    {
        await using var harness = await ServerHarness.StartAsync();
        var unparsable = await harness.AddDeviceAsync("0x0a0001");
        var configured = await harness.AddDeviceAsync("0x0a0002");
        var configuration = unparsable.DefaultConfiguration();
        configuration["endpoint"] = "not an endpoint";
        await unparsable.PublishConfigurationAsync(configuration);
        await unparsable.PublishStateAsync();
        await configured.ComeOnlineAsync();

        await AssertSkippedWhileTheOthersPlay(harness, configured, unparsable);
    }

    // The skipped device published first, so the server knew of it before the other, which
    // the doorbell still plays on from its first packet. Once the skipped device advertises
    // its endpoint, the next doorbell reaches it, its first packet the first its counter
    // numbered: nothing was sent to it before.
    private static async Task AssertSkippedWhileTheOthersPlay(
        ServerHarness harness,
        TestDevice configured,
        TestDevice skipped
    )
    {
        await harness.RingDoorbellAsync();

        Assert.Equal(
            Enumerable.Range(0, Packets),
            (await configured.NextPacketsAsync(Packets)).Select(p => p.Index)
        );

        await skipped.PublishConfigurationAsync();
        await harness.RingDoorbellAsync();

        Assert.Equal(0, (await skipped.NextPacketAsync()).Index);
    }
}
