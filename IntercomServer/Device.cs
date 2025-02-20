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

    public async Task SubscribeStream(IMqttClient client, string stream)
    {
        await client.PublishStringAsync($"intercom/client/{DeviceId}/set/subscribe_stream", stream);
    }

    public async Task UnsubscribeStream(IMqttClient client, string stream)
    {
        await client.PublishStringAsync(
            $"intercom/client/{DeviceId}/set/unsubscribe_stream",
            stream
        );
    }
}
