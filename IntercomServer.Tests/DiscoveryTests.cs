using IntercomServer.Tests.Harness;

namespace IntercomServer.Tests;

/// <summary>
/// How the server learns its devices: from the <c>configuration</c> and <c>state</c> each one
/// publishes under a prefix in <c>DEVICES_TOPIC_PREFIXES</c>, <c>intercom/client</c> and
/// <c>toilet_automation</c> by default, and nowhere else. It sends a device its commands under
/// the prefix it found the device under. A device is reached, here by the doorbell, while its
/// latest state is online and enabled. A check that a device got nothing stands on a control
/// device that the same sound, or a later one, reached: had the device been reached, its packets
/// would have gone out beside or before the control's.
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
    public async Task An_empty_state_removes_the_device_and_its_configuration_goes_with_it()
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

        // Back with its state alone, the device has no configuration, and the doorbell sends it
        // nothing. The witness's packets are that doorbell's or the next one's, which the
        // device, offline by then, is out of: a packet to the device went out before them.
        var witness = await harness.AddDeviceAsync("0x0a0003");
        await witness.ComeOnlineAsync();
        await device.PublishStateAsync();
        await harness.RingDoorbellAsync();
        await device.PublishStateAsync(online: false);
        await harness.RingDoorbellAsync();

        await witness.NextPacketsAsync(Packets);
        Assert.Equal(0, device.ReceivedPacketCount);

        await device.ComeOnlineAsync();
        await harness.RingDoorbellAsync();

        await device.NextPacketAsync();
    }

    [Fact]
    public async Task A_device_under_either_default_prefix_is_discovered_and_sent_its_commands_under_it()
    {
        await using var harness = await ServerHarness.StartAsync();
        var intercom = await harness.AddDeviceAsync("0x0a0001");
        var other = await harness.AddDeviceAsync("0x0b0001", "toilet_automation");
        await intercom.ComeOnlineAsync();
        await other.ComeOnlineAsync();

        await harness.RingDoorbellAsync();

        await intercom.NextPacketsAsync(Packets);
        await other.NextPacketsAsync(Packets);

        // A call sends each device every command there is, each on the device's own topics, and
        // the other device's clicks answer and end it.
        await Calls.StartAsync(intercom, other);
        await Calls.EndAsync(other, intercom, other);
    }

    [Fact]
    public async Task The_setting_names_the_prefixes_and_a_device_under_any_other_is_not_discovered()
    {
        await using var harness = await ServerHarness.StartAsync(
            new Dictionary<string, string?> { ["DEVICES_TOPIC_PREFIXES"] = "intercom/client, test/devices" }
        );
        var intercom = await harness.AddDeviceAsync("0x0a0001");
        var named = await harness.AddDeviceAsync("0x0c0001", "test/devices");
        var unnamed = await harness.AddDeviceAsync("0x0b0001", "toilet_automation");
        await intercom.ComeOnlineAsync();
        await named.ComeOnlineAsync();
        await unnamed.ComeOnlineAsync();
        await unnamed.ClickAsync();

        await harness.RingDoorbellAsync();

        await intercom.NextPacketsAsync(Packets);
        await named.NextPacketsAsync(Packets);
        Assert.Equal(0, unnamed.ReceivedPacketCount);

        // Had the server read the unnamed device's click, that click would have placed a call,
        // and sent the device the blink of a call being placed before the doorbell rang.
        await Calls.RequestAsync(intercom, named);
        Assert.Equal(0, unnamed.ReceivedCommandCount);
    }

    [Theory]
    [InlineData("intercom/+")]
    [InlineData("toilet_automation/#")]
    [InlineData("intercom/client,")]
    [InlineData("/intercom/client")]
    [InlineData("intercom//client")]
    [InlineData("intercom/client/")]
    [InlineData("intercom/client,intercom/client/kitchen")]
    [InlineData("intercom/server")]
    [InlineData("intercom/server/devices")]
    [InlineData("intercom")]
    public async Task A_setting_with_a_prefix_the_server_cannot_read_devices_under_stops_its_start(
        string prefixes
    )
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ServerHarness.StartAsync(new Dictionary<string, string?> { ["DEVICES_TOPIC_PREFIXES"] = prefixes })
        );

        Assert.StartsWith("DEVICES_TOPIC_PREFIXES holds ", error.Message);
    }
}
