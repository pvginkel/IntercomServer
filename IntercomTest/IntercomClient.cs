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
    private IMqttClient _client;
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

        await _client.ConnectAsync(mqttClientOptions);

        await Subscribe($"intercom/{device.DeviceId}/set/+");
        await Subscribe($"intercom/{device.DeviceId}/stream/in");

        async Task Subscribe(string topic)
        {
            await _client.SubscribeAsync(
                _factory
                    .CreateSubscribeOptionsBuilder()
                    .WithTopicFilter(f => f.WithTopic(topic))
                    .Build()
            );
        }
    }

    private async Task HandleMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        using (await _syncRoot.EnterAsync())
        {
            device.HandleMessage(GetTopicSubPath(e.ApplicationMessage.Topic), e);
        }
    }

    private string GetTopicSubPath(string topic)
    {
        var pos = topic.IndexOf('/');
        if (pos == -1)
            throw new InvalidOperationException("Unexpected topic name");
        var pos1 = topic.IndexOf('/', pos + 1);
        if (pos1 == -1)
            throw new InvalidOperationException("Unexpected topic name");
        return topic.Substring(pos1 + 1);
    }
}
