using System.Net;
using IntercomServer.Tests.Harness;

namespace IntercomServer.Tests;

/// <summary>
/// The harness end to end: the composed server against the test's broker, a device's MQTT both
/// ways, its UDP listener, and two servers that do not hear each other. What the server does
/// with what it hears is pinned elsewhere; these tests only show that the harness carries it.
/// </summary>
public sealed class HarnessTests
{
    [Fact]
    public async Task The_server_connects_to_the_tests_broker_and_reports_ready()
    {
        await using var harness = await ServerHarness.StartAsync();

        var (status, body) = await harness.GetHealthAsync("/readyz");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("mqtt: connected", body);
    }

    [Fact]
    public async Task A_device_action_reaches_the_server_and_its_answer_reaches_the_device()
    {
        await using var harness = await ServerHarness.StartAsync();
        var device = await harness.AddDeviceAsync("0x0a0001");
        await device.ComeOnlineAsync();

        await device.ClickAsync();

        var command = await device.NextCommandAsync();
        Assert.StartsWith("set/", command.Subtopic);
    }

    [Fact]
    public async Task Audio_the_server_plays_reaches_the_devices_udp_listener()
    {
        await using var harness = await ServerHarness.StartAsync();
        var device = await harness.AddDeviceAsync("0x0a0001");
        await device.ComeOnlineAsync();

        await harness.RingDoorbellAsync();

        var packet = await device.NextPacketAsync();
        Assert.True(packet.Payload.Length > 0);
    }

    [Fact]
    public async Task Two_servers_side_by_side_each_hear_only_their_own_broker_and_devices()
    {
        var starting = new[] { ServerHarness.StartAsync(), ServerHarness.StartAsync() };
        await using var first = await starting[0];
        await using var second = await starting[1];

        Assert.NotEqual(first.Broker.Port, second.Broker.Port);
        Assert.NotEqual(first.HttpPort, second.HttpPort);
        Assert.NotEqual(first.DataDirectory, second.DataDirectory);

        // The same device id on both: each server knows only the one on its own broker.
        var heard = await first.AddDeviceAsync("0x0a0001");
        var other = await second.AddDeviceAsync("0x0a0001");
        await heard.ComeOnlineAsync();
        await other.ComeOnlineAsync();

        await first.RingDoorbellAsync();

        // A quarter of a second of the doorbell, 20 ms a packet, and none of it on the other
        // server's device.
        for (var i = 0; i < 12; i++)
            await heard.NextPacketAsync();

        Assert.Equal(0, other.ReceivedPacketCount);
        Assert.Equal(0, other.ReceivedCommandCount);
    }
}
