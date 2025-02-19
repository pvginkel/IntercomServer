using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntercomServer.Audio;
using MQTTnet;

namespace IntercomServer;

[DebuggerDisplay("DeviceId = {DeviceId}, State = {State}, Configuration = {Configuration}")]
internal class Device(string deviceId)
{
    private static readonly JsonSerializerOptions JsonSerializerOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

    public AudioBuffer AudioBuffer { get; } =
        new(Constants.AudioFormat, Constants.AudioLeadBuffer, Constants.AudioTrailBuffer);

    public string DeviceId { get; } = deviceId;
    public DeviceConfiguration? Configuration { get; private set; }
    public DeviceState? State { get; private set; }

    public void ParseConfiguration(string json)
    {
        Configuration = JsonSerializer.Deserialize<DeviceConfiguration>(
            json,
            JsonSerializerOptions
        );
    }

    public void ParseState(string json)
    {
        State = JsonSerializer.Deserialize<DeviceState>(json, JsonSerializerOptions);
    }

    public async Task SetRedLed(IMqttClient client, DeviceLedAction action)
    {
        await client.PublishStringAsync(
            $"intercom/{DeviceId}/set/red_led",
            JsonSerializer.Serialize(action, JsonSerializerOptions)
        );
    }

    public async Task SetGreenLed(IMqttClient client, DeviceLedAction action)
    {
        await client.PublishStringAsync(
            $"intercom/{DeviceId}/set/green_led",
            JsonSerializer.Serialize(action, JsonSerializerOptions)
        );
    }

    public async Task SetRecording(IMqttClient client, bool value)
    {
        await client.PublishStringAsync(
            $"intercom/{DeviceId}/set/recording",
            JsonSerializer.Serialize(value, JsonSerializerOptions)
        );
    }
}

internal record DeviceConfiguration(
    string? UniqueId,
    DeviceAudioFormats? AudioFormats,
    DeviceDeviceConfiguration? Device
);

internal record DeviceAudioFormats(DeviceAudioFormat? In, DeviceAudioFormat? Out);

internal record DeviceAudioFormat(string? ChannelLayout, int? SampleRate, int? BitRate);

internal record DeviceDeviceConfiguration(string? Manufacturer, string? Model, string? Name);

internal record DeviceState(
    bool? Online,
    bool? Enabled,
    bool? RedLed,
    bool? GreenLed,
    bool? Playing,
    bool? Recording
);

internal record DeviceLedAction(
    DeviceLedState State,
    int? Duration = null,
    int? On = null,
    int? Off = null
);

internal enum DeviceLedState
{
    [JsonStringEnumMemberName("on")]
    On,

    [JsonStringEnumMemberName("off")]
    Off,

    [JsonStringEnumMemberName("blink")]
    Blink
}
