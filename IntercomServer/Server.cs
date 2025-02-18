using System.Text.RegularExpressions;
using MQTTnet;
using MQTTnet.Internal;
using Serilog;

namespace IntercomServer;

internal class Server(
    IMqttClient client,
    ServerConfiguration configuration,
    DeviceManager devices,
    StateManager state
) : IAsyncDisposable
{
    private static readonly ILogger Logger = Log.ForContext<Server>();
    private static readonly Regex TopicRe = new("^intercom/([^/]*)/(.*)$", RegexOptions.Compiled);

    private readonly MqttClientFactory _factory = new();
    private readonly AsyncLock _syncRoot = new();

    public async Task Connect()
    {
        var mqttClientOptionsBuilder = new MqttClientOptionsBuilder().WithTcpServer(
            configuration.Host,
            configuration.Port
        );

        if (!string.IsNullOrEmpty(configuration.Username))
        {
            mqttClientOptionsBuilder = mqttClientOptionsBuilder.WithCredentials(
                configuration.Username,
                configuration.Password
            );
        }

        var mqttClientOptions = mqttClientOptionsBuilder.Build();

        client.ApplicationMessageReceivedAsync += e =>
        {
            try
            {
                HandleMessage(e);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to process incoming message");
            }

            return Task.CompletedTask;
        };

        await client.ConnectAsync(mqttClientOptions);

        await Subscribe("intercom/+/state");
        await Subscribe("intercom/+/configuration");
        await Subscribe("intercom/+/set/action");
        await Subscribe("intercom/+/stream/out");

        async Task Subscribe(string topic)
        {
            var mqttSubscribeOptions = _factory
                .CreateSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic(topic))
                .Build();

            await client.SubscribeAsync(mqttSubscribeOptions, CancellationToken.None);
        }
    }

    private async void HandleMessage(MqttApplicationMessageReceivedEventArgs e)
    {
        var match = TopicRe.Match(e.ApplicationMessage.Topic);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"Cannot parse incoming message topic '{e.ApplicationMessage.Topic}'"
            );
        }

        var deviceId = match.Groups[1].Value;
        var topic = match.Groups[2].Value;

        using (await _syncRoot.EnterAsync())
        {
            var device = devices.GetById(deviceId);

            switch (topic)
            {
                case "configuration":
                    device.ParseConfiguration(e.ApplicationMessage.ConvertPayloadToString());
                    break;

                case "state":
                    device.ParseState(e.ApplicationMessage.ConvertPayloadToString());
                    break;

                case "set/action":
                    await state.HandleDeviceAction(
                        device,
                        e.ApplicationMessage.ConvertPayloadToString() switch
                        {
                            "click" => DeviceAction.Click,
                            "long_click" => DeviceAction.LongClick,
                            var action
                                => throw new InvalidOperationException(
                                    $"Unknown device action '{action}'"
                                )
                        }
                    );
                    break;

                case "stream/out":
                    device.HandleStreamData(e.ApplicationMessage.Payload);
                    break;

                default:
                    Logger.Debug(
                        "Ignoring message from device {Device} on topic {Topic}",
                        deviceId,
                        topic
                    );
                    break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await client.DisconnectAsync(_factory.CreateClientDisconnectOptionsBuilder().Build());

        client.Dispose();
    }
}
