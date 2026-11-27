using IntercomServer.Tests.Harness;
using static IntercomServer.Tests.Harness.Commands;

namespace IntercomServer.Tests;

/// <summary>
/// The doorbell, which Home Assistant rings on <c>intercom/server/set/ring_doorbell</c>, plays on
/// every device that can take a call, online, enabled and with a microphone, and on each online
/// device <c>DOORBELL_EXTRA_DEVICES</c> names, whatever call those devices are in. It plays on no
/// other. It replaces a ring in progress, and leaves the call that ring was for waiting.
/// </summary>
public sealed class DoorbellTests
{
    // A fifth of a second of the doorbell, one packet each 20 ms.
    private const int Packets = 10;

    // The doorbell's first packets to a ringing device may race the ring's last, so a tenth of a
    // second of them is left out of the comparison.
    private const int RaceAllowance = 5;

    [Fact]
    public async Task The_doorbell_reaches_every_device_that_can_take_a_call_and_no_other()
    {
        await using var harness = await ServerHarness.StartAsync();
        var first = await harness.AddDeviceAsync("0x0a0001");
        var second = await harness.AddDeviceAsync("0x0a0002");
        var third = await harness.AddDeviceAsync("0x0a0003");
        var disabled = await harness.AddDeviceAsync("0x0a0004");
        var offline = await harness.AddDeviceAsync("0x0a0005");
        var withoutMicrophone = await harness.AddDeviceAsync("0x0a0006");
        await first.ComeOnlineAsync();
        await second.ComeOnlineAsync();
        await third.ComeOnlineAsync();
        await disabled.PublishConfigurationAsync();
        await disabled.PublishStateAsync(enabled: false);
        await offline.PublishConfigurationAsync();
        await offline.PublishStateAsync(online: false);
        await withoutMicrophone.PublishConfigurationAsync(withoutMicrophone.ConfigurationWithoutMicrophone());
        await withoutMicrophone.PublishStateAsync();

        await harness.RingDoorbellAsync();

        var toFirst = await first.NextPacketsAsync(Packets);
        var toSecond = await second.NextPacketsAsync(Packets);
        var toThird = await third.NextPacketsAsync(Packets);
        Assert.True(AudioPacket.SameAudio(toFirst, toSecond), "The second device got other audio.");
        Assert.True(AudioPacket.SameAudio(toFirst, toThird), "The third device got other audio.");
        Assert.Equal(0, disabled.ReceivedPacketCount);
        Assert.Equal(0, offline.ReceivedPacketCount);
        Assert.Equal(0, withoutMicrophone.ReceivedPacketCount);
    }

    [Fact]
    public async Task The_setting_adds_the_devices_it_names_while_they_are_online_enabled_or_not()
    {
        await using var harness = await ServerHarness.StartAsync(
            new Dictionary<string, string?>
            {
                ["DOORBELL_EXTRA_DEVICES"] = " 0x0a0002 ,0x0a0003, 0x0a0004,0x0a0005,0x0a00ff",
            }
        );
        var control = await harness.AddDeviceAsync("0x0a0001");
        var withoutMicrophone = await harness.AddDeviceAsync("0x0a0002");
        var disabled = await harness.AddDeviceAsync("0x0a0003");
        var toilet = await harness.AddDeviceAsync("0x0a0004", "toilet_automation");
        var offline = await harness.AddDeviceAsync("0x0a0005");
        var unlisted = await harness.AddDeviceAsync("0x0a0006");
        await control.ComeOnlineAsync();
        await withoutMicrophone.PublishConfigurationAsync(withoutMicrophone.ConfigurationWithoutMicrophone());
        await withoutMicrophone.PublishStateAsync();
        await disabled.PublishConfigurationAsync();
        await disabled.PublishStateAsync(enabled: false);
        // A ToiletAutomation device has no microphone, and its state no enabled.
        await toilet.PublishConfigurationAsync(toilet.ConfigurationWithoutMicrophone());
        await toilet.PublishAsync("state", """{"online":true}""", retain: true);
        await offline.PublishConfigurationAsync(offline.ConfigurationWithoutMicrophone());
        await offline.PublishStateAsync(online: false);
        await unlisted.PublishConfigurationAsync(unlisted.ConfigurationWithoutMicrophone());
        await unlisted.PublishStateAsync();

        await harness.RingDoorbellAsync();

        var toControl = await control.NextPacketsAsync(Packets);
        foreach (var listed in new[] { withoutMicrophone, disabled, toilet })
            Assert.True(
                AudioPacket.SameAudio(toControl, await listed.NextPacketsAsync(Packets)),
                $"Device {listed.DeviceId} got other audio."
            );
        Assert.Equal(0, offline.ReceivedPacketCount);
        Assert.Equal(0, unlisted.ReceivedPacketCount);
    }

    [Theory]
    [InlineData("0x0a0001,")]
    [InlineData(" ")]
    [InlineData("0x0a0001,,0x0a0002")]
    [InlineData("intercom/client/0x0a0001")]
    [InlineData("+")]
    [InlineData("#")]
    public async Task A_setting_with_an_entry_that_is_no_device_id_stops_the_servers_start(string extraDevices)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ServerHarness.StartAsync(
                new Dictionary<string, string?> { ["DOORBELL_EXTRA_DEVICES"] = extraDevices }
            )
        );

        Assert.StartsWith("DOORBELL_EXTRA_DEVICES holds ", error.Message);
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
