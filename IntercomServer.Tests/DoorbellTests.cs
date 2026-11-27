using IntercomServer.Tests.Harness;
using static IntercomServer.Tests.Harness.Commands;

namespace IntercomServer.Tests;

/// <summary>
/// The doorbell, which Home Assistant rings on <c>intercom/server/set/ring_doorbell</c>, plays on
/// every device that is online and enabled, and on no other, whatever call those devices are in.
/// It replaces a ring in progress, and leaves the call that ring was for waiting.
/// </summary>
public sealed class DoorbellTests
{
    // A fifth of a second of the doorbell, one packet each 20 ms.
    private const int Packets = 10;

    // The doorbell's first packets to a ringing device may race the ring's last, so a tenth of a
    // second of them is left out of the comparison.
    private const int RaceAllowance = 5;

    [Fact]
    public async Task The_doorbell_reaches_every_online_enabled_device_and_no_other()
    {
        await using var harness = await ServerHarness.StartAsync();
        var first = await harness.AddDeviceAsync("0x0a0001");
        var second = await harness.AddDeviceAsync("0x0a0002");
        var third = await harness.AddDeviceAsync("0x0a0003");
        var disabled = await harness.AddDeviceAsync("0x0a0004");
        var offline = await harness.AddDeviceAsync("0x0a0005");
        await first.ComeOnlineAsync();
        await second.ComeOnlineAsync();
        await third.ComeOnlineAsync();
        await disabled.PublishConfigurationAsync();
        await disabled.PublishStateAsync(enabled: false);
        await offline.PublishConfigurationAsync();
        await offline.PublishStateAsync(online: false);

        await harness.RingDoorbellAsync();

        var toFirst = await first.NextPacketsAsync(Packets);
        var toSecond = await second.NextPacketsAsync(Packets);
        var toThird = await third.NextPacketsAsync(Packets);
        Assert.True(AudioPacket.SameAudio(toFirst, toSecond), "The second device got other audio.");
        Assert.True(AudioPacket.SameAudio(toFirst, toThird), "The third device got other audio.");
        Assert.Equal(0, disabled.ReceivedPacketCount);
        Assert.Equal(0, offline.ReceivedPacketCount);
    }

    [Fact]
    public async Task The_doorbell_reaches_the_devices_in_a_call_and_the_call_goes_on()
    {
        await using var harness = await ServerHarness.StartAsync();
        var caller = await harness.AddDeviceAsync("0x0a0001");
        var answerer = await harness.AddDeviceAsync("0x0a0002");
        var idle = await harness.AddDeviceAsync("0x0a0003");
        await caller.ComeOnlineAsync();
        await answerer.ComeOnlineAsync();
        await idle.ComeOnlineAsync();
        await Calls.StartAsync(caller, answerer, idle);
        await Calls.AssertRingStoppedAsync(answerer, idle);

        await harness.RingDoorbellAsync();

        var toCaller = await caller.NextPacketsAsync(Packets);
        Assert.True(AudioPacket.SameAudio(toCaller, await answerer.NextPacketsAsync(Packets)));
        Assert.True(AudioPacket.SameAudio(toCaller, await idle.NextPacketsAsync(Packets)));

        await Calls.EndAsync(caller, caller, answerer);
    }

    [Fact]
    public async Task The_doorbell_replaces_a_ring_in_progress_and_the_call_it_rang_for_still_waits()
    {
        await using var harness = await ServerHarness.StartAsync();
        var caller = await harness.AddDeviceAsync("0x0a0001");
        var called = await harness.AddDeviceAsync("0x0a0002");
        await caller.ComeOnlineAsync();
        await called.ComeOnlineAsync();
        await Calls.RequestAsync(caller, called);
        await called.NextPacketsAsync(Packets);

        await harness.RingDoorbellAsync();

        // The ring never reaches the caller, so all it gets is the doorbell, once.
        var doorbell = await caller.NextSoundAsync(BuiltInSounds.DoorbellLength + BuiltInSounds.Margin);
        Assert.InRange(
            doorbell.Sum(p => p.Payload.Length),
            BuiltInSounds.Bytes(BuiltInSounds.DoorbellLength - BuiltInSounds.Margin),
            BuiltInSounds.Bytes(BuiltInSounds.DoorbellLength + BuiltInSounds.Margin)
        );

        // The called device ends on the same doorbell, and had the ring played on beside it, or
        // after it, ring packets would sit among or after the doorbell's.
        var toCalled = called.TakeArrivedPackets();
        Assert.True(
            AudioPacket.SameAudio(
                doorbell.Skip(RaceAllowance),
                toCalled.TakeLast(doorbell.Count - RaceAllowance)
            ),
            $"The called device's last {doorbell.Count - RaceAllowance} of {toCalled.Count} packets are not the doorbell's."
        );

        // The call still waits: the called device's click accepts it.
        await called.ClickAsync();

        await caller.ExpectCommandsAsync(
            RedLed(Led.Off),
            GreenLed(Led.On),
            Recording(true),
            AddEndpoint(called.Endpoint)
        );
        await called.ExpectCommandsAsync(
            RedLed(Led.Off),
            AddEndpoint(caller.Endpoint),
            GreenLed(Led.On),
            Recording(true)
        );
    }
}
