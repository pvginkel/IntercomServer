using IntercomServer.Tests.Harness;

namespace IntercomServer.Tests;

/// <summary>
/// How the server learns its devices: from the <c>configuration</c> and <c>state</c> each one
/// publishes under <c>intercom/client</c>, and nowhere else. A device is reached, here by the
/// doorbell, while its latest state is online and enabled. A check that a device got nothing
/// stands on a control device, which the same sound reached: had the device been reached too,
/// its packets would have gone out beside the control's.
/// </summary>
public sealed class DiscoveryTests
{
    // A fifth of a second of the doorbell, one packet each 20 ms.
    private const int Packets = 10;

    [Fact]
    public async Task A_device_is_reached_once_it_has_published_both_its_configuration_and_its_state()
    {
        await using var harness = await ServerHarness.StartAsync();
        var control = await harness.AddDeviceAsync("0x0a0001");
        var device = await harness.AddDeviceAsync("0x0a0002");
        await control.ComeOnlineAsync();
        await device.PublishConfigurationAsync();

        await harness.RingDoorbellAsync();

        await control.NextPacketsAsync(Packets);
        Assert.Equal(0, device.ReceivedPacketCount);

        await device.PublishStateAsync();
        await harness.RingDoorbellAsync();

        await device.NextPacketAsync();
    }

    [Fact]
    public async Task A_devices_latest_state_decides_whether_it_is_reached()
    {
        await using var harness = await ServerHarness.StartAsync();
        var control = await harness.AddDeviceAsync("0x0a0001");
        var disabled = await harness.AddDeviceAsync("0x0a0002");
        var offline = await harness.AddDeviceAsync("0x0a0003");
        await control.ComeOnlineAsync();
        await disabled.PublishConfigurationAsync();
        await disabled.PublishStateAsync(enabled: false);
        await offline.ComeOnlineAsync();
        await offline.PublishStateAsync(online: false);

        await harness.RingDoorbellAsync();

        await control.NextPacketsAsync(Packets);
        Assert.Equal(0, disabled.ReceivedPacketCount);
        Assert.Equal(0, offline.ReceivedPacketCount);

        await disabled.PublishStateAsync();
        await offline.PublishStateAsync();
        await harness.RingDoorbellAsync();

        await disabled.NextPacketAsync();
        await offline.NextPacketAsync();
    }

    [Fact]
    public async Task An_empty_configuration_removes_the_device_and_its_state_goes_with_it()
    {
        await using var harness = await ServerHarness.StartAsync();
        var control = await harness.AddDeviceAsync("0x0a0001");
        var device = await harness.AddDeviceAsync("0x0a0002");
        await control.ComeOnlineAsync();
        await device.ComeOnlineAsync();
        await device.PublishConfigurationAsync(null);

        await harness.RingDoorbellAsync();

        await control.NextPacketsAsync(Packets);
        Assert.Equal(0, device.ReceivedPacketCount);

        // The control's packets from here on may be the first doorbell's; a device that comes
        // online now gets only the next one's.
        var witness = await harness.AddDeviceAsync("0x0a0003");
        await witness.ComeOnlineAsync();
        await device.PublishConfigurationAsync();
        await harness.RingDoorbellAsync();

        await witness.NextPacketsAsync(Packets);
        Assert.Equal(0, device.ReceivedPacketCount);

        await device.PublishStateAsync();
        await harness.RingDoorbellAsync();

        await device.NextPacketAsync();
    }

    [Fact]
    public async Task An_empty_state_removes_the_device()
    {
        await using var harness = await ServerHarness.StartAsync();
        var control = await harness.AddDeviceAsync("0x0a0001");
        var device = await harness.AddDeviceAsync("0x0a0002");
        await control.ComeOnlineAsync();
        await device.ComeOnlineAsync();
        await device.PublishAsync("state", null, retain: true);

        await harness.RingDoorbellAsync();

        await control.NextPacketsAsync(Packets);
        Assert.Equal(0, device.ReceivedPacketCount);

        await device.ComeOnlineAsync();
        await harness.RingDoorbellAsync();

        await device.NextPacketAsync();
    }

    [Fact]
    public async Task A_device_under_another_prefix_is_not_discovered()
    {
        await using var harness = await ServerHarness.StartAsync();
        var intercom = await harness.AddDeviceAsync("0x0a0001");
        var other = await harness.AddDeviceAsync("0x0b0001", "toilet_automation");
        await intercom.ComeOnlineAsync();
        await other.ComeOnlineAsync();
        await other.ClickAsync();

        await harness.RingDoorbellAsync();

        await intercom.NextPacketsAsync(Packets);
        Assert.Equal(0, other.ReceivedPacketCount);

        // Had the server known the other device, the intercom's click would ring it; had the
        // other device's click acted, it would have rung the intercom first. Either way the
        // intercom's first command would not be "call not possible".
        await intercom.ClickAsync();

        (await intercom.NextCommandAsync()).AssertIs("set/red_led", """{"state":"on","duration":1000}""");
        Assert.Equal(0, other.ReceivedCommandCount);
    }
}
