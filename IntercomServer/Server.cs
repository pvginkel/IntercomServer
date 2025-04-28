using System.ComponentModel;
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
    AlarmManager alarmManager,
    PlaybackManager playbackManager
) : IAsyncDisposable
{
    private static readonly ILogger Logger = Log.ForContext<Server>();
    private static readonly Regex TopicRe =
        new("^intercom/(?:server|client/([^/]*))/(.*)$", RegexOptions.Compiled);

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

        var result = await client.ConnectAsync(mqttClientOptions);
        if (result.ResultCode != MqttClientConnectResultCode.Success)
        {
            throw new InvalidOperationException(
                $"Failed to connect to the MQTT server with error '{result.ResultCode}'"
            );
        }

        await client.SubscribeAsync("intercom/server/set/+");
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
            if (deviceId.Length == 0)
            {
                HandleServerMessage(topic, e.ApplicationMessage);
            }
            else
            {
                var device = devices.GetById(deviceId);

                await HandleClientMessage(topic, device, e.ApplicationMessage);
            }
        }
    }

    private void HandleServerMessage(string topic, MqttApplicationMessage message)
    {
        switch (topic)
        {
            case "set/ring_doorbell":
                if (message.ConvertPayloadToString() == "true")
                {
                    playbackManager.StartPlayback(
                        devices.GetAllEnabled(),
                        Constants.AudioFiles.Doorbell
                    );
                }
                break;

            case "set/auto_accept":
                state.IsAutoAccept = message.ConvertPayloadToString() == "true";
                break;

            default:
                Logger.Debug("Ignoring message for server on topic {Topic}", topic);
                break;
        }
    }

    private async Task HandleClientMessage(
        string topic,
        Device device,
        MqttApplicationMessage message
    )
    {
        switch (topic)
        {
            case "configuration":
                var payload = message.ConvertPayloadToString();

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
                payload = message.ConvertPayloadToString();

                if (payload == null)
                {
                    devices.Remove(device);
                    break;
                }

                device.ParseState(payload);

                Logger.Information(
                    "Received state for device {Device} state {State}",
                    device.DeviceId,
                    device.State
                );
                break;

            case "set/action":
                var deviceAction = message.ConvertPayloadToString() switch
                {
                    "click" => DeviceAction.Click,
                    "long_click" => DeviceAction.LongClick,
                    var action
                        => throw new InvalidOperationException($"Unknown device action '{action}'")
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
                    device.DeviceId,
                    topic
                );
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await client.DisconnectAsync(_factory.CreateClientDisconnectOptionsBuilder().Build());

        client.Dispose();
    }
}
