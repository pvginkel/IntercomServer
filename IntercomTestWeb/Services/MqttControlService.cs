using System.Text.Json;
using System.Text.RegularExpressions;
using IntercomServer.Utils;
using IntercomTestWeb.Models;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using Serilog;

namespace IntercomTestWeb.Services;

// The control-plane MQTT client, ported from IntercomTest's MainWindow. It discovers real devices by
// subscribing to the retained configuration/state topics, maintains the DeviceRegistry, pushes every
// change to the browser over /ws/events, and publishes the set/* commands the REST API triggers.
//
// Each REST handler is a thin pass-through to a publish here — the MQTT contract stays the single
// source of truth (no shadow state). Reconnect handling follows IntercomServer.Server (the WPF app
// did not reconnect, but this is a daemon).
public sealed partial class MqttControlService(
    ServerConfiguration configuration,
    DeviceRegistry registry,
    SimDeviceManager simDevices,
    EventHub hub,
    SettingsStore settings
) : IHostedService
{
    private static readonly ILogger Logger = Log.ForContext<MqttControlService>();
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    [GeneratedRegex("^intercom/client/([^/]+)/(configuration|state)$")]
    private static partial Regex TopicRegex();

    private readonly MqttClientFactory _factory = new();
    private readonly IMqttClient _client = new MqttClientFactory().CreateMqttClient();
    private readonly CancellationTokenSource _shutdownCts = new();

    private MqttClientOptions? _options;
    private int _reconnecting;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(configuration.Host, configuration.Port)
            .WithWillRetain();

        if (!string.IsNullOrEmpty(configuration.Username))
        {
            builder = builder.WithCredentials(configuration.Username, configuration.Password);
        }

        _options = builder.Build();

        _client.ApplicationMessageReceivedAsync += OnApplicationMessage;
        _client.DisconnectedAsync += OnDisconnected;

        // Don't block host startup (or take the process down) if the broker is briefly unavailable;
        // fall back to the reconnect loop.
        _ = Task.Run(async () =>
        {
            try
            {
                await ConnectAndSubscribe(_shutdownCts.Token);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Initial MQTT connection failed; retrying in the background");
                StartReconnectLoop();
            }
        });

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdownCts.CancelAsync();

        try
        {
            await _client.DisconnectAsync(
                _factory.CreateClientDisconnectOptionsBuilder().Build()
            );
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Error disconnecting the control client during shutdown");
        }

        _shutdownCts.Dispose();
        _client.Dispose();
    }

    private async Task ConnectAndSubscribe(CancellationToken cancellationToken)
    {
        if (!_client.IsConnected)
            await _client.ConnectAsync(_options!, cancellationToken);

        // Push the persisted auto-accept on every (re)connect, matching MainWindow's behavior.
        await _client.PublishStringAsync(
            "intercom/server/set/auto_accept",
            settings.AutoAccept ? "true" : "false",
            cancellationToken: cancellationToken
        );

        await _client.SubscribeAsync(
            "intercom/client/+/configuration",
            cancellationToken: cancellationToken
        );
        await _client.SubscribeAsync(
            "intercom/client/+/state",
            cancellationToken: cancellationToken
        );
    }

    private Task OnApplicationMessage(MqttApplicationMessageReceivedEventArgs arg)
    {
        var match = TopicRegex().Match(arg.ApplicationMessage.Topic);
        if (!match.Success)
        {
            Logger.Warning("Invalid topic {Topic}", arg.ApplicationMessage.Topic);
            return Task.CompletedTask;
        }

        var deviceId = match.Groups[1].Value;
        var action = match.Groups[2].Value;

        // A simulated device publishes its own configuration/state; the SimDeviceManager already
        // mirrors those to the browser, so the control client ignores them here (matching the WPF
        // filter that kept simulated ids out of the real-device list).
        if (simDevices.IsSimDevice(deviceId))
            return Task.CompletedTask;

        var payload = arg.ApplicationMessage.ConvertPayloadToString();

        try
        {
            switch (action)
            {
                case "configuration":
                    if (string.IsNullOrEmpty(payload))
                    {
                        registry.Remove(deviceId);
                        hub.Broadcast(new DeviceRemovedMessage("real", deviceId));
                    }
                    else
                    {
                        var config = JsonSerializer.Deserialize<DeviceConfiguration>(
                            payload,
                            IntercomJson.Options
                        )!;

                        registry.UpsertConfig(deviceId, config);
                        hub.Broadcast(new DeviceConfigMessage("real", deviceId, config));
                    }
                    break;

                case "state":
                    if (!string.IsNullOrEmpty(payload))
                    {
                        var state = JsonSerializer.Deserialize<DeviceState>(
                            payload,
                            IntercomJson.Options
                        )!;

                        registry.UpsertState(deviceId, state);
                        hub.Broadcast(new DeviceStateMessage("real", deviceId, state));
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to handle message on {Topic}", arg.ApplicationMessage.Topic);
        }

        return Task.CompletedTask;
    }

    // ---- Commands (REST pass-throughs). Each publishes to the MQTT contract. ----

    public Task SetVolumeAsync(string id, double volume) =>
        _client.PublishStringAsync(
            $"intercom/client/{id}/set/volume",
            JsonSerializer.Serialize(volume, IntercomJson.Options)
        );

    public Task SetEnabledAsync(string id, bool enabled) =>
        _client.PublishStringAsync(
            $"intercom/client/{id}/set/enabled",
            enabled ? "true" : "false"
        );

    public Task IdentifyAsync(string id) =>
        _client.PublishStringAsync($"intercom/client/{id}/set/identify", "true");

    public Task RestartAsync(string id) =>
        _client.PublishStringAsync($"intercom/client/{id}/set/restart", "true");

    public Task SetAudioConfigAsync(string id, AudioConfiguration audioConfig) =>
        _client.PublishStringAsync(
            $"intercom/client/{id}/set/audio_config",
            JsonSerializer.Serialize(audioConfig, IntercomJson.Options)
        );

    public async Task RemoveRealDeviceAsync(string id)
    {
        await _client.PublishBinaryAsync($"intercom/client/{id}/state", retain: true);
        await _client.PublishBinaryAsync($"intercom/client/{id}/configuration", retain: true);
    }

    public Task RingDoorbellAsync() =>
        _client.PublishStringAsync("intercom/server/set/ring_doorbell", "true");

    public async Task SetAutoAcceptAsync(bool enabled)
    {
        settings.AutoAccept = enabled;

        await _client.PublishStringAsync(
            "intercom/server/set/auto_accept",
            enabled ? "true" : "false"
        );

        hub.Broadcast(new ServerSettingsMessage(enabled));
    }

    // ---- Reconnect (mirrors IntercomServer.Server) ----

    private Task OnDisconnected(MqttClientDisconnectedEventArgs e)
    {
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

                if (!_shutdownCts.IsCancellationRequested && !_client.IsConnected)
                    StartReconnectLoop();
            }
        });
    }

    private async Task ReconnectLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ReconnectDelay, cancellationToken);

                if (_client.IsConnected)
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
                Logger.Warning(ex, "MQTT reconnect attempt failed; retrying in {Delay}", ReconnectDelay);
            }
        }
    }
}
