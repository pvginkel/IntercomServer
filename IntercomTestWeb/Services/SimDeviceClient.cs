using System.Text.Json;
using System.Text.Json.Nodes;
using IntercomServer.Utils;
using MQTTnet;
using MQTTnet.Internal;
using Serilog;

namespace IntercomTestWeb.Services;

// Ported from IntercomTest's IntercomClient: the MQTT identity for one simulated device. Connects
// with a retained LWT of {online:false}, publishes the device's retained configuration + state,
// subscribes to its set/+ commands, and republishes state whenever the device changes.
//
// Added over the WPF original: a simple auto-reconnect loop, because this is now a long-running
// daemon rather than a desktop app you restart. (The WPF client never reconnected.)
public sealed class SimDeviceClient : IAsyncDisposable
{
    private static readonly ILogger Logger = Log.ForContext<SimDeviceClient>();
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly SimDevice _device;
    private readonly ServerConfiguration _configuration;
    private readonly MqttClientFactory _factory = new();
    private readonly IMqttClient _client;
    private readonly AsyncLock _syncRoot = new();
    private readonly CancellationTokenSource _shutdownCts = new();

    private MqttClientOptions? _options;
    private int _reconnecting;

    public SimDeviceClient(SimDevice device, ServerConfiguration configuration)
    {
        _device = device;
        _configuration = configuration;
        _client = _factory.CreateMqttClient();
    }

    public async Task ConnectAsync()
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_configuration.Host, _configuration.Port)
            .WithWillTopic($"intercom/client/{_device.DeviceId}/state")
            .WithWillPayload(new JsonObject { ["online"] = false }.ToJsonString())
            .WithWillRetain();

        if (!string.IsNullOrEmpty(_configuration.Username))
        {
            builder = builder.WithCredentials(_configuration.Username, _configuration.Password);
        }

        _options = builder.Build();

        _client.ApplicationMessageReceivedAsync += async e =>
        {
            try
            {
                using (await _syncRoot.EnterAsync())
                {
                    _device.HandleMessage(e);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Failed to process incoming message");
            }
        };

        _device.StateChanged += OnDeviceStateChanged;

        // The low-level client does not reconnect on its own; own the loop so a dropped connection
        // is restored automatically.
        _client.DisconnectedAsync += OnDisconnected;

        await ConnectAndAnnounce(_shutdownCts.Token);
    }

    private async Task ConnectAndAnnounce(CancellationToken cancellationToken)
    {
        if (!_client.IsConnected)
            await _client.ConnectAsync(_options!, cancellationToken);

        // Subscriptions and retained announcements don't survive a clean-session reconnect, so
        // (re)establish them on every successful connect.
        await _client.SubscribeAsync(
            $"intercom/client/{_device.DeviceId}/set/+",
            cancellationToken: cancellationToken
        );
        await _client.SubscribeAsync(
            $"intercom/client/{_device.DeviceId}/stream/in",
            cancellationToken: cancellationToken
        );

        await _client.PublishStringAsync(
            $"intercom/client/{_device.DeviceId}/configuration",
            JsonSerializer.Serialize(_device.GetConfiguration(), IntercomJson.Options),
            retain: true,
            cancellationToken: cancellationToken
        );

        await PublishState(cancellationToken);
    }

    private async void OnDeviceStateChanged(object? sender, EventArgs e)
    {
        try
        {
            await PublishState(_shutdownCts.Token);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Failed to publish state");
        }
    }

    private Task PublishState(CancellationToken cancellationToken) =>
        _client.PublishStringAsync(
            $"intercom/client/{_device.DeviceId}/state",
            JsonSerializer.Serialize(_device.GetState(), IntercomJson.Options),
            retain: true,
            cancellationToken: cancellationToken
        );

    public Task SendActionAsync(DeviceAction action) =>
        _client.PublishStringAsync(
            $"intercom/client/{_device.DeviceId}/set/action",
            action switch
            {
                DeviceAction.Click => "click",
                DeviceAction.LongClick => "long_click",
                _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
            }
        );

    public async Task RemoveDevice()
    {
        // Clearing the retained configuration + state is how a device is removed from the bus.
        await _client.PublishBinaryAsync(
            $"intercom/client/{_device.DeviceId}/state",
            retain: true
        );
        await _client.PublishBinaryAsync(
            $"intercom/client/{_device.DeviceId}/configuration",
            retain: true
        );
    }

    private Task OnDisconnected(MqttClientDisconnectedEventArgs e)
    {
        if (_shutdownCts.IsCancellationRequested)
            return Task.CompletedTask;

        if (Interlocked.CompareExchange(ref _reconnecting, 1, 0) != 0)
            return Task.CompletedTask;

        Logger.Warning(
            e.Exception,
            "Sim device {Device} disconnected (reason {Reason}); reconnecting",
            _device.DeviceId,
            e.Reason
        );

        _ = Task.Run(async () =>
        {
            try
            {
                await ReconnectLoop(_shutdownCts.Token);
            }
            finally
            {
                Interlocked.Exchange(ref _reconnecting, 0);
            }
        });

        return Task.CompletedTask;
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

                await ConnectAndAnnounce(cancellationToken);

                Logger.Information("Sim device {Device} reconnected", _device.DeviceId);
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Logger.Warning(
                    ex,
                    "Sim device {Device} reconnect attempt failed; retrying",
                    _device.DeviceId
                );
            }
        }
    }

    public async ValueTask DisposeAsync()
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
            Logger.Debug(ex, "Error disconnecting sim device {Device}", _device.DeviceId);
        }

        _shutdownCts.Dispose();
        _client.Dispose();
    }
}
