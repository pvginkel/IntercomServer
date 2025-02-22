using System.Text.RegularExpressions;
using IntercomServer.Utils;
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
    private static readonly Regex TopicRe =
        new("^intercom/client/([^/]*)/(.*)$", RegexOptions.Compiled);

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

        await client.SubscribeAsync("intercom/client/+/state");
        await client.SubscribeAsync("intercom/client/+/configuration");
        await client.SubscribeAsync("intercom/client/+/set/action");
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
                    var payload = e.ApplicationMessage.ConvertPayloadToString();

                    if (payload == null)
                    {
                        devices.Remove(device);
                        break;
                    }

                    device.ParseConfiguration(payload);

                    Logger.Information(
                        "Received configuration for device {Device} configuration {Configuration}",
                        device.DeviceId,
                        device.Configuration
                    );
                    break;

                case "state":
                    payload = e.ApplicationMessage.ConvertPayloadToString();

                    if (payload == null)
                    {
                        devices.Remove(device);
                        break;
                    }

                    var wasEnabled = device.State?.Enabled == true;

                    device.ParseState(payload);

                    Logger.Information(
                        "Received state for device {Device} state {State}",
                        device.DeviceId,
                        device.State
                    );

                    var isEnabled = device.State?.Enabled == true;

                    if (wasEnabled != isEnabled)
                        await DeviceEnabledChanged(device, isEnabled);
                    break;

                case "set/action":
                    var deviceAction = e.ApplicationMessage.ConvertPayloadToString() switch
                    {
                        "click" => DeviceAction.Click,
                        "long_click" => DeviceAction.LongClick,
                        var action
                            => throw new InvalidOperationException(
                                $"Unknown device action '{action}'"
                            )
                    };

                    Logger.Information(
                        "Received action for device {Device} action {DeviceAction}",
                        device.DeviceId,
                        deviceAction
                    );

                    await state.HandleDeviceAction(device, deviceAction);
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

    private async Task DeviceEnabledChanged(Device device, bool enabled)
    {
        if (enabled)
            await device.SubscribeStream(client, "intercom/server/stream/global");
        else
            await device.UnsubscribeStream(client, "intercom/server/stream/global");
    }

    public async ValueTask DisposeAsync()
    {
        await client.DisconnectAsync(_factory.CreateClientDisconnectOptionsBuilder().Build());

        client.Dispose();
    }
}
