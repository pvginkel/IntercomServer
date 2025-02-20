using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using IntercomServer.Utils;
using MQTTnet;
using MQTTnet.Internal;
using Serilog;

namespace IntercomTest;

internal class IntercomClient(Device device, ServerConfiguration configuration)
{
    private static readonly JsonSerializerOptions JsonSerializerOptions =
        new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

    private static readonly ILogger Logger = Log.ForContext<IntercomClient>();

    private readonly MqttClientFactory _factory = new();
    private IMqttClient _client = default!;
    private readonly AsyncLock _syncRoot = new();

    public async Task Connect()
    {
        _client = _factory.CreateMqttClient();

        var mqttClientOptionsBuilder = new MqttClientOptionsBuilder()
            .WithTcpServer(configuration.Host, configuration.Port)
            .WithWillPayload(new JsonObject { ["online"] = false }.ToJsonString())
            .WithWillRetain();

        if (!string.IsNullOrEmpty(configuration.Username))
        {
            mqttClientOptionsBuilder = mqttClientOptionsBuilder.WithCredentials(
                configuration.Username,
                configuration.Password
            );
        }

        var mqttClientOptions = mqttClientOptionsBuilder.Build();

        _client.ApplicationMessageReceivedAsync += async e =>
        {
            try
            {
                await HandleMessage(e);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to process incoming message");
            }
        };

        device.SubscribedStream += Device_SubscribedStream;
        device.UnsubscribedStream += Device_UnsubscribedStream;

        await _client.ConnectAsync(mqttClientOptions);

        await _client.SubscribeAsync($"intercom/client/{device.DeviceId}/set/+");
    }

    private async void Device_SubscribedStream(object? sender, DeviceStreamEventArgs e)
    {
        try
        {
            await _client.SubscribeAsync(e.Stream);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to subscribe to stream");
        }
    }

    private async void Device_UnsubscribedStream(object? sender, DeviceStreamEventArgs e)
    {
        try
        {
            await _client.UnsubscribeAsync(e.Stream);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to unsubscribe to stream");
        }
    }

    private async Task HandleMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        using (await _syncRoot.EnterAsync())
        {
            device.HandleMessage(e);
        }
    }
}
