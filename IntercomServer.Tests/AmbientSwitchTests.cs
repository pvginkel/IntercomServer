using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using IntercomServer.Tests.Harness;
using Microsoft.Extensions.DependencyInjection;

namespace IntercomServer.Tests;

/// <summary>
/// The "Ambient sound" switch and the "Ambient track" sensor, as Home Assistant meets them over
/// the broker. Each device under <c>AMBIENT_TOPIC_PREFIXES</c> gets both, retained, on its own
/// page, its switch off as the server finds it. The switch's <c>ON</c> and <c>OFF</c> start and
/// stop the ambient sound, heard at the device's UDP listener, and the state follows the playback
/// whatever starts or ends it. A retained command is ignored, and a refused start leaves the
/// switch off. The server says it is online, and offline by its will or at a clean stop, and a
/// removed device's switch and sensor are emptied.
/// </summary>
public sealed class AmbientSwitchTests
{
    private static readonly TimeSpan TrackLength = TimeSpan.FromSeconds(1);

    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_ambient_device_gets_a_retained_switch_and_sensor_on_its_own_page_its_switch_off_as_it_is_found_and_an_intercom_gets_neither()
    {
        await using var harness = await ServerHarness.StartAsync();
        var observer = await harness.ObserveAsync("homeassistant/#", "intercom/server/devices/#");
        var intercom = await harness.AddDeviceAsync("0x0a0001");
        await intercom.ComeOnlineAsync();
        var toilet = await harness.AddDeviceAsync("0x0b0001", DeviceApi.ToiletPrefix);
        // A server that stopped without its shutdown left the switch on.
        await harness.PublishAsync(
            HomeAssistant.StateTopic(toilet.DeviceId),
            HomeAssistant.On("Rain").ToJsonString(),
            retain: true
        );
        await toilet.PublishConfigurationAsync(DeviceApi.ToiletConfiguration(toilet));
        await toilet.PublishStateAsync();

        // The server publishes in the order it reads the devices, the intercom's first.
        var before = new List<ObservedMessage>();
        ObservedMessage message;
        while ((message = await observer.NextAsync()).Topic != HomeAssistant.SwitchTopic(toilet.DeviceId))
            before.Add(message);
        Assert.Single(before).AssertIs(HomeAssistant.StateTopic(toilet.DeviceId), HomeAssistant.On("Rain"));
        var uniqueId = DeviceApi.UniqueId(toilet);
        message.AssertIs(HomeAssistant.SwitchTopic(toilet.DeviceId), HomeAssistant.SwitchDiscovery(toilet, uniqueId));
        (await observer.NextAsync()).AssertIs(
            HomeAssistant.SensorTopic(toilet.DeviceId),
            HomeAssistant.SensorDiscovery(toilet, uniqueId)
        );
        (await observer.NextAsync()).AssertIs(HomeAssistant.StateTopic(toilet.DeviceId), HomeAssistant.Off());

        (await harness.RetainedAsync(HomeAssistant.SwitchTopic(toilet.DeviceId))).AssertIs(
            HomeAssistant.SwitchTopic(toilet.DeviceId),
            HomeAssistant.SwitchDiscovery(toilet, uniqueId)
        );
        (await harness.RetainedAsync(HomeAssistant.SensorTopic(toilet.DeviceId))).AssertIs(
            HomeAssistant.SensorTopic(toilet.DeviceId),
            HomeAssistant.SensorDiscovery(toilet, uniqueId)
        );
        (await harness.RetainedAsync(HomeAssistant.StateTopic(toilet.DeviceId))).AssertIs(
            HomeAssistant.StateTopic(toilet.DeviceId),
            HomeAssistant.Off()
        );
    }

    [Fact]
    public async Task The_switch_starts_and_stops_the_ambient_sound_and_the_retained_state_says_so_with_the_track()
    {
        await using var harness = await ServerHarness.StartAsync();
        var toilet = await harness.AddToiletAsync("0x0b0001");
        var rain = await Tracks.WhiteNoiseAsync("wav", TrackLength);
        await harness.UploadAsync("Rain", rain);
        var state = HomeAssistant.StateTopic(toilet.DeviceId);
        var observer = await harness.ObserveAsync(state);
        (await observer.NextAsync()).AssertIs(state, HomeAssistant.Off());

        await harness.SwitchAsync(toilet.DeviceId, "ON");
        (await observer.NextAsync()).AssertIs(state, HomeAssistant.On("Rain"));
        (await harness.RetainedAsync(state)).AssertIs(state, HomeAssistant.On("Rain"));
        // Past the fade-in and the loop's seam.
        var packets = await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(1.2)));
        await harness.SwitchAsync(toilet.DeviceId, "OFF");
        packets.AddRange(await AmbientAudio.RestAsync(toilet));
        (await observer.NextAsync()).AssertIs(state, HomeAssistant.Off());
        (await harness.RetainedAsync(state)).AssertIs(state, HomeAssistant.Off());

        AmbientAudio.AssertNumbered(packets, first: 0);
        AmbientAudio.AssertOnePlayback(
            AmbientAudio.Samples(packets),
            await AmbientAudio.DecodedAsync(rain, DeviceApi.ToiletFormat),
            AmbientAudio.Fade,
            fadedOut: true
        );
    }

    [Theory]
    [InlineData("routes")]
    [InlineData("cap")]
    [InlineData("offline")]
    [InlineData("shutdown")]
    public async Task The_state_follows_the_playback_whether_the_routes_the_cap_the_device_going_offline_or_the_shutdown_ends_it(
        string end
    )
    {
        await using var harness = await ServerHarness.StartAsync(
            end == "cap" ? new Dictionary<string, string?> { ["AMBIENT_CAP_SECONDS"] = "1" } : null
        );
        const string deviceId = "0x0b0001";
        var state = HomeAssistant.StateTopic(deviceId);
        var observer = await harness.ObserveAsync(HomeAssistant.StatusTopic, state);
        (await observer.NextAsync()).AssertIs(HomeAssistant.StatusTopic, "online");
        var toilet = await harness.AddToiletAsync(deviceId);
        var rain = await Tracks.WhiteNoiseAsync("wav", TrackLength);
        await harness.UploadAsync("Rain", rain);
        (await observer.NextAsync()).AssertIs(state, HomeAssistant.Off());

        if (end == "routes")
            Assert.Equal("Rain", await harness.PlayAmbientAsync(toilet));
        else
            await harness.SwitchAsync(deviceId, "ON");
        (await observer.NextAsync()).AssertIs(state, HomeAssistant.On("Rain"));
        var packets = await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(0.6)));

        await (
            end switch
            {
                "routes" => harness.EndAmbientAsync(toilet),
                "offline" => toilet.PublishStateAsync(online: false),
                "shutdown" => harness.StopServerAsync(),
                _ => Task.CompletedTask,
            }
        );
        packets.AddRange(await AmbientAudio.RestAsync(toilet));
        (await observer.NextAsync()).AssertIs(state, HomeAssistant.Off());

        AmbientAudio.AssertOnePlayback(
            AmbientAudio.Samples(packets),
            await AmbientAudio.DecodedAsync(rain, DeviceApi.ToiletFormat),
            AmbientAudio.Fade,
            fadedOut: true
        );

        if (end == "shutdown")
            (await observer.NextAsync()).AssertIs(HomeAssistant.StatusTopic, "offline");

        // A device gone offline keeps its switch and sensor, which its state shows unavailable.
        if (end == "offline")
        {
            var uniqueId = DeviceApi.UniqueId(toilet);
            (await harness.RetainedAsync(HomeAssistant.SwitchTopic(deviceId))).AssertIs(
                HomeAssistant.SwitchTopic(deviceId),
                HomeAssistant.SwitchDiscovery(toilet, uniqueId)
            );
            (await harness.RetainedAsync(HomeAssistant.SensorTopic(deviceId))).AssertIs(
                HomeAssistant.SensorTopic(deviceId),
                HomeAssistant.SensorDiscovery(toilet, uniqueId)
            );
        }
    }

    [Theory]
    [InlineData("configuration")]
    [InlineData("state")]
    public async Task A_removed_device_has_its_switch_sensor_and_state_emptied_and_its_playback_s_end_publishes_nothing_after(
        string removal
    )
    {
        await using var harness = await ServerHarness.StartAsync();
        const string deviceId = "0x0b0001";
        var observer = await harness.ObserveAsync("homeassistant/#", HomeAssistant.StateTopic(deviceId));
        var toilet = await harness.AddToiletAsync(deviceId);
        await harness.UploadAsync("Rain", await Tracks.WhiteNoiseAsync("wav", TrackLength));
        var uniqueId = DeviceApi.UniqueId(toilet);

        await harness.SwitchAsync(deviceId, "ON");
        await toilet.NextPacketsAsync(AmbientAudio.Packets(TimeSpan.FromSeconds(0.6)));
        await (
            removal == "configuration"
                ? toilet.PublishConfigurationAsync(null)
                : toilet.PublishAsync("state", null, retain: true)
        );
        await AmbientAudio.RestAsync(toilet);

        // Back, the device is found again: whatever its playback's end published would come before.
        await toilet.PublishConfigurationAsync(DeviceApi.ToiletConfiguration(toilet));
        await toilet.PublishStateAsync();

        (string Topic, string Payload)[] found =
        [
            (HomeAssistant.SwitchTopic(deviceId), HomeAssistant.SwitchDiscovery(toilet, uniqueId).ToJsonString()),
            (HomeAssistant.SensorTopic(deviceId), HomeAssistant.SensorDiscovery(toilet, uniqueId).ToJsonString()),
            (HomeAssistant.StateTopic(deviceId), HomeAssistant.Off().ToJsonString()),
        ];
        (string Topic, string Payload)[] expected =
        [
            .. found,
            (HomeAssistant.StateTopic(deviceId), HomeAssistant.On("Rain").ToJsonString()),
            (HomeAssistant.SwitchTopic(deviceId), ""),
            (HomeAssistant.SensorTopic(deviceId), ""),
            (HomeAssistant.StateTopic(deviceId), ""),
            .. found,
        ];
        foreach (var (topic, payload) in expected)
        {
            var message = await observer.NextAsync();
            if (payload.Length == 0)
                message.AssertIs(topic, "");
            else
                message.AssertIs(topic, JsonNode.Parse(payload)!);
        }
    }

    [Fact]
    public async Task A_refused_start_leaves_the_switch_off()
    {
        await using var harness = await ServerHarness.StartAsync();
        const string deviceId = "0x0b0001";
        var state = HomeAssistant.StateTopic(deviceId);
        var observer = await harness.ObserveAsync(state);
        var toilet = await harness.AddToiletAsync(deviceId);
        (await observer.NextAsync()).AssertIs(state, HomeAssistant.Off());

        // No track to play.
        await harness.SwitchAsync(deviceId, "ON");
        (await observer.NextAsync()).AssertIs(state, HomeAssistant.Off());

        await harness.UploadAsync("Rain", await Tracks.WhiteNoiseAsync("wav", TrackLength));
        await harness.SwitchAsync(deviceId, "ON");
        (await observer.NextAsync()).AssertIs(state, HomeAssistant.On("Rain"));
        await toilet.NextPacketAsync();
    }

    [Fact]
    public async Task The_server_is_online_while_connected_offline_by_its_will_once_its_connection_is_lost_and_offline_at_a_clean_stop()
    {
        await using var harness = await ServerHarness.StartAsync();
        var observer = await harness.ObserveAsync(HomeAssistant.StatusTopic);
        var online = await observer.NextAsync();
        online.AssertIs(HomeAssistant.StatusTopic, "online");
        Assert.True(online.Retained);

        await harness.Broker.DropServerAsync();
        (await observer.NextAsync()).AssertIs(HomeAssistant.StatusTopic, "offline");
        (await harness.RetainedAsync(HomeAssistant.StatusTopic)).AssertIs(HomeAssistant.StatusTopic, "offline");
        // The server connects again on its own.
        (await observer.NextAsync()).AssertIs(HomeAssistant.StatusTopic, "online");

        await harness.StopServerAsync();
        (await observer.NextAsync()).AssertIs(HomeAssistant.StatusTopic, "offline");
        (await harness.RetainedAsync(HomeAssistant.StatusTopic)).AssertIs(HomeAssistant.StatusTopic, "offline");
    }

    [Fact]
    public async Task A_retained_command_is_ignored_so_a_stale_ON_starts_nothing_when_the_server_subscribes_again()
    {
        await using var harness = await ServerHarness.StartAsync();
        const string deviceId = "0x0b0001";
        await harness.UploadAsync("Rain", await Tracks.WhiteNoiseAsync("wav", TrackLength, seed: 1));
        await harness.UploadAsync("Snow", await Tracks.WhiteNoiseAsync("wav", TrackLength, seed: 2));
        await harness.TrackStore().UpdateAsync("Snow", null, TrackState.Disabled, Cancellation);
        // Retained while the server knows no such device, so the ON it hears now starts nothing.
        await harness.SwitchAsync(deviceId, "ON", retain: true);
        var toilet = await harness.AddToiletAsync(deviceId);
        var observer = await harness.ObserveAsync(HomeAssistant.SwitchTopic(deviceId));
        await observer.NextAsync();

        // On its new subscription the broker sends the server the retained ON, then the toilet's
        // configuration, whose discovery the server publishes again.
        await harness.Broker.DropServerAsync();
        (await observer.NextAsync()).AssertIs(
            HomeAssistant.SwitchTopic(deviceId),
            HomeAssistant.SwitchDiscovery(toilet, DeviceApi.UniqueId(toilet))
        );

        // A start the ON had begun would play Rain, the one Ambient track, and this one would join it.
        Assert.Equal("Snow", await harness.PlayAmbientAsync(toilet, "Snow"));
        Assert.Equal(0, (await toilet.NextPacketAsync()).Index);
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task A_start_s_decode_holds_up_none_of_the_server_s_other_messages_the_doorbell_s_included()
    {
        using var ffmpeg = new HeldFfmpeg();
        await using var harness = await ServerHarness.StartAsync(
            services: services => services.AddSingleton(new AudioDecoder(ffmpeg.Executable))
        );
        var toilet = await harness.AddToiletAsync("0x0b0001");
        var intercom = await harness.AddDeviceAsync("0x0a0001");
        await intercom.ComeOnlineAsync();
        // Small enough for FFmpeg's input pipe to take whole while FFmpeg is held.
        await harness.UploadAsync("Rain", await Tracks.WhiteNoiseAsync("mp3", TrackLength));
        var state = HomeAssistant.StateTopic(toilet.DeviceId);
        var observer = await harness.ObserveAsync(state);
        (await observer.NextAsync()).AssertIs(state, HomeAssistant.Off());

        ffmpeg.Hold();
        await harness.SwitchAsync(toilet.DeviceId, "ON");
        await ffmpeg.HeldAsync();
        await harness.RingDoorbellAsync();
        await intercom.NextPacketAsync();

        ffmpeg.Release();
        (await observer.NextAsync()).AssertIs(state, HomeAssistant.On("Rain"));
        await toilet.NextPacketAsync();
    }
}
