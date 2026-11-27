using IntercomServer.Tests.Harness;
using static IntercomServer.Tests.Harness.Commands;

namespace IntercomServer.Tests;

/// <summary>
/// Nothing reads whether a device has a microphone. An online, enabled device whose
/// configuration advertises an <c>out</c> format and no <c>in</c> is rung by a call request and
/// reached by the doorbell, and its click and long click act as any other device's do.
/// </summary>
public sealed class MicrophoneTests
{
    // A fifth of a second of a sound, one packet each 20 ms.
    private const int Packets = 10;

    [Fact]
    public async Task A_device_without_a_microphone_is_rung_accepts_and_ends_the_call_as_any_other()
    {
        await using var harness = await ServerHarness.StartAsync();
        var intercom = await harness.AddDeviceAsync("0x0a0001");
        var withoutMicrophone = await AddDeviceWithoutMicrophoneAsync(harness, "0x0a0002");
        await intercom.ComeOnlineAsync();

        await Calls.RequestAsync(intercom, withoutMicrophone);
        await withoutMicrophone.NextPacketsAsync(Packets);

        await withoutMicrophone.ClickAsync();

        await intercom.ExpectCommandsAsync(
            RedLed(Led.Off),
            GreenLed(Led.On),
            Recording(true),
            AddEndpoint(withoutMicrophone.Endpoint)
        );
        await withoutMicrophone.ExpectCommandsAsync(
            RedLed(Led.Off),
            AddEndpoint(intercom.Endpoint),
            GreenLed(Led.On),
            Recording(true)
        );

        await Calls.EndAsync(withoutMicrophone, intercom, withoutMicrophone);
    }

    [Fact]
    public async Task The_doorbell_reaches_a_device_without_a_microphone()
    {
        await using var harness = await ServerHarness.StartAsync();
        var intercom = await harness.AddDeviceAsync("0x0a0001");
        var withoutMicrophone = await AddDeviceWithoutMicrophoneAsync(harness, "0x0a0002");
        await intercom.ComeOnlineAsync();

        await harness.RingDoorbellAsync();

        Assert.True(
            AudioPacket.SameAudio(
                await intercom.NextPacketsAsync(Packets),
                await withoutMicrophone.NextPacketsAsync(Packets)
            )
        );
    }

    [Fact]
    public async Task A_device_without_a_microphone_calls_cancels_and_starts_a_conversation_as_any_other()
    {
        await using var harness = await ServerHarness.StartAsync();
        var intercom = await harness.AddDeviceAsync("0x0a0001");
        var withoutMicrophone = await AddDeviceWithoutMicrophoneAsync(harness, "0x0a0002");
        await intercom.ComeOnlineAsync();

        await Calls.RequestAsync(withoutMicrophone, intercom);
        await intercom.NextPacketsAsync(Packets);
        await withoutMicrophone.ClickAsync();

        await withoutMicrophone.ExpectCommandsAsync(RedLed(Led.Off));
        await intercom.ExpectCommandsAsync(RedLed(Led.Off));

        await withoutMicrophone.LongClickAsync();

        await withoutMicrophone.ExpectCommandsAsync(
            AddEndpoint(harness.AudioEndpoint),
            GreenLed(Led.On),
            Recording(true),
            RemoveEndpoint(harness.AudioEndpoint),
            Recording(false),
            GreenLed(Led.Off)
        );
        await Calls.AssertNoCallAsync(intercom, withoutMicrophone);
    }

    [Fact]
    public async Task A_device_without_a_microphone_joins_a_call_in_progress_with_a_click_as_any_other()
    {
        await using var harness = await ServerHarness.StartAsync();
        var caller = await harness.AddDeviceAsync("0x0a0001");
        var answerer = await harness.AddDeviceAsync("0x0a0002");
        var withoutMicrophone = await AddDeviceWithoutMicrophoneAsync(harness, "0x0a0003");
        await caller.ComeOnlineAsync();
        await answerer.ComeOnlineAsync();
        await Calls.StartAsync(caller, answerer, withoutMicrophone);

        await Calls.JoinAsync(withoutMicrophone, caller, answerer);

        await Calls.EndAsync(withoutMicrophone, caller, answerer, withoutMicrophone);
        await Calls.AssertNoCallAsync(caller, answerer, withoutMicrophone);
    }

    [Fact]
    public async Task A_device_without_a_microphone_rejects_a_ringing_call_with_a_long_click_as_any_other()
    {
        await using var harness = await ServerHarness.StartAsync();
        var caller = await harness.AddDeviceAsync("0x0a0001");
        var other = await harness.AddDeviceAsync("0x0a0002");
        var withoutMicrophone = await AddDeviceWithoutMicrophoneAsync(harness, "0x0a0003");
        await caller.ComeOnlineAsync();
        await other.ComeOnlineAsync();
        await Calls.RequestAsync(caller, other, withoutMicrophone);
        await other.NextPacketsAsync(Packets);

        await withoutMicrophone.LongClickAsync();

        await caller.ExpectCommandsAsync(RedLed(Led.Off), RedLed(Led.Rejected));
        await other.ExpectCommandsAsync(RedLed(Led.Off));
        await withoutMicrophone.ExpectCommandsAsync(RedLed(Led.Off));
        await Calls.AssertNoCallAsync(other, caller, withoutMicrophone);
    }

    // A device with no configuration advertises no microphone either.
    [Fact]
    public async Task A_device_with_a_state_and_no_configuration_is_rung_and_places_a_call_as_any_other()
    {
        await using var harness = await ServerHarness.StartAsync();
        var intercom = await harness.AddDeviceAsync("0x0a0001");
        var unconfigured = await harness.AddDeviceAsync("0x0a0002");
        await unconfigured.PublishStateAsync();
        await intercom.ComeOnlineAsync();

        await Calls.RequestAsync(intercom, unconfigured);
        await intercom.ClickAsync();

        await intercom.ExpectCommandsAsync(RedLed(Led.Off));
        await unconfigured.ExpectCommandsAsync(RedLed(Led.Off));

        await Calls.RequestAsync(unconfigured, intercom);
    }

    private static async Task<TestDevice> AddDeviceWithoutMicrophoneAsync(
        ServerHarness harness,
        string deviceId
    )
    {
        var device = await harness.AddDeviceAsync(deviceId);
        var configuration = device.DefaultConfiguration();
        configuration["audio_formats"]!.AsObject().Remove("in");
        await device.PublishConfigurationAsync(configuration);
        await device.PublishStateAsync();
        return device;
    }
}
