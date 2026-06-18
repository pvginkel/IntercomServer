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

    private static readonly TimeSpan InitialReconnectDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);

    private readonly MqttClientFactory _factory = new();
    private readonly AsyncLock _syncRoot = new();
    private readonly CancellationTokenSource _shutdownCts = new();

    private MqttClientOptions? _options;
    private int _reconnecting; // 0 = no reconnect loop running, 1 = one is running.
    private int _disposed; // 0 = live, 1 = DisposeAsync has run (guards a double dispose).
    private volatile bool _subscribed; // True once connected *and* our subscriptions are in place.

    /// <summary>
    /// Whether the server is connected to the MQTT broker and its subscriptions are active.
    /// Used by the readiness probe.
    /// </summary>
    public bool IsConnected => client.IsConnected && _subscribed;

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

        _options = mqttClientOptionsBuilder.Build();

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

        // The low-level MQTT client does not reconnect on its own: when the broker connection
        // drops it raises DisconnectedAsync and otherwise stays down. Own the reconnect loop so a
        // lost connection is restored automatically (this is what failed in production before).
        client.DisconnectedAsync += OnDisconnected;

        try
        {
            await ConnectAndSubscribe(_shutdownCts.Token);
        }
        catch (Exception ex)
        {
            // Don't take the process down if the broker is briefly unavailable at startup; fall
            // back to the same reconnect loop that handles a mid-flight disconnect. The readiness
            // probe reports "not ready" until the connection is established.
            Logger.Error(ex, "Initial MQTT connection failed; retrying in the background");
            StartReconnectLoop();
        }
    }

    private async Task ConnectAndSubscribe(CancellationToken cancellationToken)
    {
        _subscribed = false;

        // Skip the connect when the socket is already up but our subscriptions are not (a previous
        // attempt connected, then failed before subscribing): reconnecting an already-connected
        // client would throw, so fall through and just (re)subscribe below.
        if (!client.IsConnected)
        {
            var result = await client.ConnectAsync(_options!, cancellationToken);
            if (result.ResultCode != MqttClientConnectResultCode.Success)
            {
                throw new InvalidOperationException(
                    $"Failed to connect to the MQTT server with error '{result.ResultCode}'"
                );
            }
        }

        // Subscriptions don't survive a reconnect (clean session), so (re)establish them on every
        // successful connect.
        await client.SubscribeAsync("intercom/server/set/+", cancellationToken: cancellationToken);
        await client.SubscribeAsync("intercom/client/+/state", cancellationToken: cancellationToken);
        await client.SubscribeAsync("intercom/client/+/ready", cancellationToken: cancellationToken);
        await client.SubscribeAsync(
            "intercom/client/+/configuration",
            cancellationToken: cancellationToken
        );
        await client.SubscribeAsync(
            "intercom/client/+/set/action",
            cancellationToken: cancellationToken
        );

        _subscribed = true;
    }

    private Task OnDisconnected(MqttClientDisconnectedEventArgs e)
    {
        _subscribed = false;

        if (_shutdownCts.IsCancellationRequested)
            return Task.CompletedTask;

        Logger.Warning(
            e.Exception,
            "MQTT connection lost (reason {Reason}); starting automatic reconnect",
            e.Reason
        );

        StartReconnectLoop();

        return Task.CompletedTask;
    }

    private void StartReconnectLoop()
    {
        // Only ever run one reconnect loop at a time. A reconnect attempt that fails before a
        // connection is established throws inside the loop (and does not re-enter here), while a
        // disconnect that arrives during the loop is a no-op because the guard is already set.
        if (Interlocked.CompareExchange(ref _reconnecting, 1, 0) != 0)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await ReconnectLoop(_shutdownCts.Token);
            }
            finally
            {
                Interlocked.Exchange(ref _reconnecting, 0);

                // Close the race where a disconnect arrived between the loop finishing and the
                // guard being cleared: that disconnect's StartReconnectLoop would have no-op'd, so
                // re-check here and restart if we're still meant to be connected.
                if (!_shutdownCts.IsCancellationRequested && !client.IsConnected)
                    StartReconnectLoop();
            }
        });
    }

    private async Task ReconnectLoop(CancellationToken cancellationToken)
    {
        var delay = InitialReconnectDelay;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, cancellationToken);

                // Already fully healthy (connected *and* subscribed) via an earlier iteration? Done.
                // Checking only client.IsConnected here would abandon a connection that came up but
                // never finished subscribing, leaving the server connected yet deaf.
                if (client.IsConnected && _subscribed)
                    return;

                Logger.Information("Attempting to reconnect to the MQTT server");
                await ConnectAndSubscribe(cancellationToken);

                Logger.Information("Reconnected to the MQTT server");
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.Warning(ex, "MQTT reconnect attempt failed; retrying in {Delay}", delay);

                delay = TimeSpan.FromSeconds(
                    Math.Min(MaxReconnectDelay.TotalSeconds, delay.TotalSeconds * 2)
                );
            }
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

                await state.HandleDeviceState(device);
                break;

            case "ready":
                if (message.ConvertPayloadToString() == "true")
                {
                    Logger.Information("Device {Device} reported ready", device.DeviceId);

                    await state.HandleDeviceReady(device);
                }
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
        // Disposed both explicitly (Service.StopAsync) and by the DI container at host shutdown;
        // run the teardown only once so the second call doesn't touch the disposed _shutdownCts.
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Stop the reconnect loop and tell OnDisconnected this disconnect is intentional, so it
        // doesn't try to bring the connection back up while we're shutting down.
        await _shutdownCts.CancelAsync();

        try
        {
            await client.DisconnectAsync(_factory.CreateClientDisconnectOptionsBuilder().Build());
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Error while disconnecting from the MQTT server during shutdown");
        }

        _shutdownCts.Dispose();
        client.Dispose();
    }
}
