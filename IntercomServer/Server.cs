using System.Text.RegularExpressions;
using MQTTnet;
using MQTTnet.Internal;
using Serilog;

namespace IntercomServer;

internal class Server(
    IMqttClient client,
    ServerConfiguration configuration,
    DeviceManager devices,
    StateManager state,
    AlarmManager alarmManager
) : IAsyncDisposable
{
    private static readonly ILogger Logger = Log.ForContext<Server>();
    private static readonly Regex TopicRe = new("^intercom/([^/]*)/(.*)$", RegexOptions.Compiled);

    private readonly MqttClientFactory _factory = new();
    private readonly AsyncLock _syncRoot = new();

    public async Task Connect()
    {
        alarmManager.AlarmExpired += AlarmManager_AlarmExpired;

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

        client.ApplicationMessageReceivedAsync += async e =>
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

    private async void AlarmManager_AlarmExpired(object? sender, AlarmExpiredEventArgs e)
    {
        try
        {
            using (await _syncRoot.EnterAsync())
            {
                await e.Action();
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error while handling alarm");
        }
    }

    private async Task HandleMessage(MqttApplicationMessageReceivedEventArgs e)
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
                    device.AudioBuffer.Append(e.ApplicationMessage.Payload);
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
