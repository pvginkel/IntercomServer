using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntercomServer.Utils;
using IntercomServer.Utils.Audio;
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

    public string DeviceId { get; } = deviceId;
    public DeviceConfiguration? Configuration { get; private set; }
    public DeviceState? State { get; private set; }

    /// <summary>
    /// The format everything sent to the device is in: its advertised <c>audio_formats.out</c>,
    /// or null when it advertises none, or one the server cannot produce.
    /// </summary>
    public AudioFormat? OutputFormat => Supported(Configuration?.AudioFormats?.Out);

    /// <summary>
    /// The format of the device's microphone: its advertised <c>audio_formats.in</c>, or null when
    /// it advertises none, or one the server cannot take.
    /// </summary>
    public AudioFormat? InputFormat => Supported(Configuration?.AudioFormats?.In);

    // The server's audio paths carry mono 16-bit PCM. The rates span telephony to studio audio,
    // and bound what a configuration from the network makes the server decode and hold.
    private static AudioFormat? Supported(DeviceAudioFormat? format) =>
        format is
        {
            ChannelLayout: "mono",
            SampleRate: { } sampleRate and >= 8000 and <= 192000,
            BitRate: 16,
        }
            ? new AudioFormat(AudioChannelLayout.Mono, sampleRate, 16)
            : null;

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
            $"intercom/client/{DeviceId}/set/red_led",
            JsonSerializer.Serialize(action, JsonSerializerOptions)
        );
    }

    public async Task SetGreenLed(IMqttClient client, DeviceLedAction action)
    {
        await client.PublishStringAsync(
            $"intercom/client/{DeviceId}/set/green_led",
            JsonSerializer.Serialize(action, JsonSerializerOptions)
        );
    }

    public async Task SetRecording(IMqttClient client, bool value)
    {
        await client.PublishStringAsync(
            $"intercom/client/{DeviceId}/set/recording",
            JsonSerializer.Serialize(value, JsonSerializerOptions)
        );
    }

    public async Task AddEndpoint(IMqttClient client, string endpoint)
    {
        await client.PublishStringAsync($"intercom/client/{DeviceId}/set/add_endpoint", endpoint);
    }

    public async Task RemoveEndpoint(IMqttClient client, string endpoint)
    {
        await client.PublishStringAsync(
            $"intercom/client/{DeviceId}/set/remove_endpoint",
            endpoint
        );
    }
}
