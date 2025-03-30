using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using IntercomServer.Utils;
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
