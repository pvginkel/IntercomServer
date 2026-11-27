using System.Diagnostics;
using IntercomServer.Tests.Harness;

namespace IntercomServer.Tests;

/// <summary>
/// How a sound plays: a looped one, the ring a call request plays to the devices it rings,
/// goes on until it is cancelled. The server plays one sound at a time, so a new one cancels
/// the one before it on every device, the devices it does not reach included. A sound to
/// devices of which one cannot be sent to reaches none of them.
/// </summary>
public sealed class PlaybackTests
{
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
    public async Task A_new_sound_cancels_the_one_before_it_on_every_device()
    {
        await using var harness = await ServerHarness.StartAsync();
        var caller = await harness.AddDeviceAsync("0x0a0001");
        var called = await harness.AddDeviceAsync("0x0a0002");
        await caller.ComeOnlineAsync();
        await called.ComeOnlineAsync();

        await caller.ClickAsync();
        await called.NextPacketsAsync(5);
        // Disabled, the called device rings on, and the doorbell does not reach it.
        await called.PublishStateAsync(enabled: false);

        await harness.RingDoorbellAsync();

        var doorbell = await caller.NextPacketsAsync(25);

        // One counter numbers every packet the server sends, so an index orders the sends. The
        // ring's last send can race the doorbell's first.
        var ringedOn = called.TakeArrivedPackets().Count(p => p.Index > doorbell[0].Index);
        Assert.True(ringedOn <= 1, $"The ring sent {ringedOn} packets after the doorbell started.");
    }

    [Fact]
    public async Task A_sound_to_a_device_without_a_configuration_reaches_none_and_the_next_one_plays()
    {
        await using var harness = await ServerHarness.StartAsync();
        var configured = await harness.AddDeviceAsync("0x0a0001");
        var unconfigured = await harness.AddDeviceAsync("0x0a0002");
        await configured.ComeOnlineAsync();
        await unconfigured.PublishStateAsync();

        await harness.RingDoorbellAsync();
        await unconfigured.PublishConfigurationAsync();
        await harness.RingDoorbellAsync();

        await AssertNothingWentOutBefore(configured, unconfigured);
    }

    [Fact]
    public async Task A_sound_to_a_device_whose_configuration_has_no_endpoint_reaches_none_and_the_next_one_plays()
    {
        await using var harness = await ServerHarness.StartAsync();
        var configured = await harness.AddDeviceAsync("0x0a0001");
        var noEndpoint = await harness.AddDeviceAsync("0x0a0002");
        await configured.ComeOnlineAsync();
        var configuration = noEndpoint.DefaultConfiguration();
        configuration.Remove("endpoint");
        await noEndpoint.PublishConfigurationAsync(configuration);
        await noEndpoint.PublishStateAsync();

        await harness.RingDoorbellAsync();
        await noEndpoint.PublishConfigurationAsync();
        await harness.RingDoorbellAsync();

        await AssertNothingWentOutBefore(configured, noEndpoint);
    }

    // The two devices' first packets are the first two the server ever sent, numbered 0 and 1
    // by the one counter: the first doorbell sent nothing, to either of them.
    private static async Task AssertNothingWentOutBefore(TestDevice first, TestDevice second)
    {
        var indices = new[] { (await first.NextPacketAsync()).Index, (await second.NextPacketAsync()).Index };

        Assert.Equal([0, 1], indices.Order());
    }
}
