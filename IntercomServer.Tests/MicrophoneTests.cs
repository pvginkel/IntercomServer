using IntercomServer.Tests.Harness;
using static IntercomServer.Tests.Harness.Commands;

namespace IntercomServer.Tests;

/// <summary>
/// A device whose configuration advertises no <c>in</c> format has no microphone, nor does one
/// with no configuration. It is never offered a call: a call request does not ring it. Its
/// click and long click do nothing: they place, join, accept and reject no call, start no
/// conversation, and leave a ringing call to its alarm. The doorbell reaches it only when
/// <c>DOORBELL_EXTRA_DEVICES</c> names it (<c>DoorbellTests</c>).
/// </summary>
public sealed class MicrophoneTests
{
    // A fifth of a second of a sound, one packet each 20 ms.
    private const int Packets = 10;

    [Fact]
    public async Task A_call_request_never_rings_a_device_without_a_microphone()
    {
        await using var harness = await ServerHarness.StartAsync();
        var caller = await harness.AddDeviceAsync("0x0a0001");
        var withoutMicrophone = await AddDeviceWithoutMicrophoneAsync(harness, "0x0a0002");
        var unconfigured = await harness.AddDeviceAsync("0x0a0003");
        await unconfigured.PublishStateAsync();
        await caller.ComeOnlineAsync();

        await caller.ClickAsync();

        await caller.ExpectCommandsAsync(RedLed(Led.CallNotPossible));

        // With an intercom to ring, the ring plays to it alone.
        var called = await harness.AddDeviceAsync("0x0a0004");
        await called.ComeOnlineAsync();
        await Calls.RequestAsync(caller, called);
        await called.NextPacketsAsync(Packets);

        Assert.Equal(0, withoutMicrophone.ReceivedPacketCount);
        await harness.AssertSentNoCommandAsync(withoutMicrophone);
        await harness.AssertSentNoCommandAsync(unconfigured);
    }

    // A call's audio goes from device to device, so the server need not take the format.
    [Fact]
    public async Task A_device_advertising_an_in_format_the_server_cannot_take_has_a_microphone_and_is_rung()
    {
        await using var harness = await ServerHarness.StartAsync();
        var caller = await harness.AddDeviceAsync("0x0a0001");
        var stereo = await harness.AddDeviceAsync("0x0a0002");
        var configuration = stereo.DefaultConfiguration();
        configuration["audio_formats"]!["in"]!["channel_layout"] = "stereo";
        await stereo.PublishConfigurationAsync(configuration);
        await stereo.PublishStateAsync();
        await caller.ComeOnlineAsync();

        await Calls.RequestAsync(caller, stereo);
    }

    [Fact]
    public async Task A_device_without_a_microphone_neither_places_a_call_nor_starts_a_conversation()
    {
        await using var harness = await ServerHarness.StartAsync();
        var intercom = await harness.AddDeviceAsync("0x0a0001");
        var withoutMicrophone = await AddDeviceWithoutMicrophoneAsync(harness, "0x0a0002");
        var unconfigured = await harness.AddDeviceAsync("0x0a0003");
        await unconfigured.PublishStateAsync();
        await intercom.ComeOnlineAsync();

        await withoutMicrophone.ClickAsync();
        await withoutMicrophone.LongClickAsync();
        await unconfigured.ClickAsync();
        await unconfigured.LongClickAsync();

        // Had a click placed a call, it would have rung the intercom, and the intercom's click
        // would accept it.
        await intercom.ClickAsync();

        await intercom.ExpectCommandsAsync(RedLed(Led.CallNotPossible));
        await harness.AssertSentNoCommandAsync(withoutMicrophone);
        await harness.AssertSentNoCommandAsync(unconfigured);
    }

    [Fact]
    public async Task A_device_without_a_microphone_does_not_join_a_call_in_progress()
    {
        await using var harness = await ServerHarness.StartAsync();
        var caller = await harness.AddDeviceAsync("0x0a0001");
        var answerer = await harness.AddDeviceAsync("0x0a0002");
        var withoutMicrophone = await AddDeviceWithoutMicrophoneAsync(harness, "0x0a0003");
        await caller.ComeOnlineAsync();
        await answerer.ComeOnlineAsync();
        await Calls.StartAsync(caller, answerer);

        await withoutMicrophone.ClickAsync();

        // Had it joined, the next command to each in the call would be its endpoint.
        await Calls.EndAsync(caller, caller, answerer);
        await harness.AssertSentNoCommandAsync(withoutMicrophone);
    }

    [Fact]
    public async Task A_device_without_a_microphone_neither_accepts_nor_rejects_a_ringing_call_and_its_alarm_still_ends_it()
    {
        await using var harness = await ServerHarness.StartAsync();
        var caller = await harness.AddDeviceAsync("0x0a0001");
        var called = await harness.AddDeviceAsync("0x0a0002");
        var withoutMicrophone = await AddDeviceWithoutMicrophoneAsync(harness, "0x0a0003");
        await caller.ComeOnlineAsync();
        await called.ComeOnlineAsync();
        await Calls.RequestAsync(caller, called);
        await called.NextPacketsAsync(Packets);

        await withoutMicrophone.ClickAsync();
        await withoutMicrophone.LongClickAsync();

        // The call rings on until the alarm takes its LEDs off. An accept would have gone on to
        // light the caller's green LED, and a reject to blink its red one.
        await caller.ExpectCommandsAsync(RedLed(Led.Off));
        await called.ExpectCommandsAsync(RedLed(Led.Off));
        await Calls.AssertNoCallAsync(called, caller);
        await harness.AssertSentNoCommandAsync(withoutMicrophone);
    }

    private static async Task<TestDevice> AddDeviceWithoutMicrophoneAsync(
        ServerHarness harness,
        string deviceId
    )
    {
        var device = await harness.AddDeviceAsync(deviceId);
        await device.PublishConfigurationAsync(device.ConfigurationWithoutMicrophone());
        await device.PublishStateAsync();
        return device;
    }
}
