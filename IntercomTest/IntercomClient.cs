using System;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using IntercomServer.Utils;
using MQTTnet;
using MQTTnet.Internal;
using Serilog;

namespace IntercomTest;

internal class IntercomClient(Device device, ServerConfiguration configuration) : IAsyncDisposable
{
    public static readonly JsonSerializerOptions JsonSerializerOptions =
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

        device.StateChanged += async (_, _) =>
        {
            try
            {
                await PublishState();
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to publish state");
            }
        };

        await _client.ConnectAsync(mqttClientOptions);

        await _client.SubscribeAsync($"intercom/client/{device.DeviceId}/set/+");

        await _client.PublishStringAsync(
            $"intercom/client/{device.DeviceId}/configuration",
            JsonSerializer.Serialize(device.GetConfiguration(), JsonSerializerOptions),
            retain: true
        );

        await PublishState();

        async Task PublishState()
        {
            await _client.PublishStringAsync(
                $"intercom/client/{device.DeviceId}/state",
                JsonSerializer.Serialize(device.GetState(), JsonSerializerOptions),
                retain: true
            );
        }
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

    public async Task SendAction(DeviceAction action)
    {
        await _client.PublishStringAsync(
            $"intercom/client/{device.DeviceId}/set/action",
            action switch
            {
                DeviceAction.Click => "click",
                DeviceAction.LongClick => "long_click",
                _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
            }
        );
    }

    public async Task SendAudio(IEnumerable<byte> buffer)
    {
        await _client.PublishBinaryAsync($"intercom/client/{device.DeviceId}/stream/out", buffer);
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisconnectAsync(_factory.CreateClientDisconnectOptionsBuilder().Build());

        _client.Dispose();
    }
}
