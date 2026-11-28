using IntercomServer.Tests.Harness;
using static IntercomServer.Tests.Harness.Commands;

namespace IntercomServer.Tests;

/// <summary>
/// A click with no call waiting or on asks for a call. It rings every other device that can take
/// one, online, enabled and with a microphone (<c>MicrophoneTests</c>): each one's red LED blinks,
/// as the caller's does, and the ring plays to them in a loop. With nobody to ring, the caller's red LED says the call is not possible. A
/// ringing call ends when the caller clicks again, when 15 seconds pass (<c>UnansweredCallTests</c>),
/// when a device accepts it (<c>CallTests</c>), or when a long click rejects it, which plays the
/// rejected tone to the caller. A device removed from the server while it is rung is rung no more.
/// </summary>
public sealed class CallRequestTests
{
    // A fifth of a second of the ring, one packet each 20 ms.
    private const int Packets = 10;

    [Fact]
    public async Task A_click_rings_every_other_online_enabled_device_with_a_blinking_red_led_and_the_looped_ring()
    {
        await using var harness = await ServerHarness.StartAsync();
        var caller = await harness.AddDeviceAsync("0x0a0001");
        var first = await harness.AddDeviceAsync("0x0a0002");
        var second = await harness.AddDeviceAsync("0x0a0003");
        var disabled = await harness.AddDeviceAsync("0x0a0004");
        await caller.ComeOnlineAsync();
        await first.ComeOnlineAsync();
        await second.ComeOnlineAsync();
        await disabled.PublishConfigurationAsync();
        await disabled.PublishStateAsync(enabled: false);

        await caller.ClickAsync();

        await caller.ExpectCommandsAsync(RedLed(Led.Calling));
        await first.ExpectCommandsAsync(RedLed(Led.Calling));
        await second.ExpectCommandsAsync(RedLed(Led.Calling));

        // The ring plays to the devices it rings, the same sound to each, and not to the caller.
        var toFirst = await first.NextPacketsAsync(Packets);
        Assert.True(AudioPacket.SameAudio(toFirst, await second.NextPacketsAsync(Packets)));
        Assert.Equal(0, caller.ReceivedPacketCount);
        Assert.Equal(0, disabled.ReceivedPacketCount);
        // Had it been rung, its LED command would have gone out before the ring started.
        Assert.Equal(0, disabled.ReceivedCommandCount);
    }

    [Fact]
    public async Task A_click_with_nobody_to_ring_shows_call_not_possible_and_leaves_no_call_waiting()
    {
        await using var harness = await ServerHarness.StartAsync();
        var caller = await harness.AddDeviceAsync("0x0a0001");
        var disabled = await harness.AddDeviceAsync("0x0a0002");
        var offline = await harness.AddDeviceAsync("0x0a0003");
        await caller.ComeOnlineAsync();
        await disabled.PublishConfigurationAsync();
        await disabled.PublishStateAsync(enabled: false);
        await offline.PublishConfigurationAsync();
        await offline.PublishStateAsync(online: false);

        await caller.ClickAsync();

        await caller.ExpectCommandsAsync(RedLed(Led.CallNotPossible));

        // Had a call been left waiting, the next click would cancel it.
        await disabled.PublishStateAsync();
        await Calls.RequestAsync(caller, disabled);
    }

    [Fact]
    public async Task The_caller_cancels_with_a_second_click()
    {
        await using var harness = await ServerHarness.StartAsync();
        var caller = await harness.AddDeviceAsync("0x0a0001");
        var called = await harness.AddDeviceAsync("0x0a0002");
        await caller.ComeOnlineAsync();
        await called.ComeOnlineAsync();
        await Calls.RequestAsync(caller, called);
        await called.NextPacketsAsync(Packets);

        await caller.ClickAsync();

        await caller.ExpectCommandsAsync(RedLed(Led.Off));
        await called.ExpectCommandsAsync(RedLed(Led.Off));
        await Calls.AssertRingStoppedAsync(called);
        await Calls.AssertNoCallAsync(called, caller);
    }

    [Fact]
    public async Task A_device_removed_while_a_call_rings_it_is_rung_no_more_so_the_cancel_sends_it_nothing()
    {
        await using var harness = await ServerHarness.StartAsync();
        var caller = await harness.AddDeviceAsync("0x0a0001");
        var called = await harness.AddDeviceAsync("0x0a0002");
        var removed = await harness.AddDeviceAsync("0x0a0003");
        await caller.ComeOnlineAsync();
        await called.ComeOnlineAsync();
        await removed.ComeOnlineAsync();
        await Calls.RequestAsync(caller, called, removed);

        await removed.PublishConfigurationAsync(null);
        await caller.ClickAsync();

        await caller.ExpectCommandsAsync(RedLed(Led.Off));
        await called.ExpectCommandsAsync(RedLed(Led.Off));
        await harness.AssertSentNoCommandAsync(removed);
    }

    [Fact]
    public async Task A_long_click_while_a_call_rings_rejects_it_and_plays_the_rejected_tone_to_the_caller()
    {
        await using var harness = await ServerHarness.StartAsync();
        var caller = await harness.AddDeviceAsync("0x0a0001");
        var rejecter = await harness.AddDeviceAsync("0x0a0002");
        var other = await harness.AddDeviceAsync("0x0a0003");
        await caller.ComeOnlineAsync();
        await rejecter.ComeOnlineAsync();
        await other.ComeOnlineAsync();
        await Calls.RequestAsync(caller, rejecter, other);
        await rejecter.NextPacketsAsync(Packets);

        await rejecter.LongClickAsync();

        await caller.ExpectCommandsAsync(RedLed(Led.Off), RedLed(Led.Rejected));
        await rejecter.ExpectCommandsAsync(RedLed(Led.Off));
        await other.ExpectCommandsAsync(RedLed(Led.Off));
        await Calls.AssertRingStoppedAsync(rejecter, other);

        // The ring never reached the caller, so what it gets is the rejected tone, played once.
        var tone = await caller.NextSoundAsync(BuiltInSounds.RejectedLength + BuiltInSounds.Margin);
        Assert.InRange(
            tone.Sum(p => p.Payload.Length),
            BuiltInSounds.Bytes(BuiltInSounds.RejectedLength - BuiltInSounds.Margin),
            BuiltInSounds.Bytes(BuiltInSounds.RejectedLength + BuiltInSounds.Margin)
        );

        await Calls.AssertNoCallAsync(other, caller, rejecter);
    }
}
