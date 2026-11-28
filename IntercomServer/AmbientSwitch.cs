using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using MQTTnet.Protocol;
using Serilog;

namespace IntercomServer;

/// <summary>
/// The "Ambient sound" switch and the "Ambient track" sensor Home Assistant shows, through MQTT
/// discovery, on the page of each device under <see cref="AmbientConfiguration.TopicPrefixes"/>.
/// Their discovery goes out, retained, as the server reads such a device's configuration, and is
/// emptied when the device is removed. The device's state goes out, retained, on
/// <c>intercom/server/devices/&lt;device_id&gt;/state</c> as its playback's sound begins and as
/// the playback ends, whatever started or stopped it, and as a start from the switch is refused.
/// The switch's <c>ON</c> and <c>OFF</c> come on <c>…/set/ambient</c>. Both entities are available
/// while the server's <see cref="Server.StatusTopic"/> and the device's own state say both are
/// online. Every publication goes out in turn, from a task of the switch's own, with what holds
/// when it goes, so the last one out holds what holds now, and none holds up the server's
/// messages.
/// </summary>
internal sealed class AmbientSwitch : IHostedService
{
    private static readonly ILogger Logger = Log.ForContext<AmbientSwitch>();

    /// <summary>The topics the switch's commands come on, one per device.</summary>
    public const string CommandFilter = "intercom/server/devices/+/set/ambient";

    // The node id of every discovery topic the server publishes. The firmware empties, on every
    // connect, each retained discovery config under its own node id, its device id, that it did
    // not publish itself.
    private const string NodeId = "intercom_server";

    private readonly IMqttClient _client;
    private readonly AmbientConfiguration _configuration;
    private readonly DeviceManager _devices;
    private readonly AmbientPlayer _player;

    // Each publication, by device id; Discover also publishes, or empties, the discovery.
    private readonly Channel<(string DeviceId, bool Discover)> _publications =
        Channel.CreateUnbounded<(string, bool)>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _publishing;

    public AmbientSwitch(IMqttClient client, AmbientConfiguration configuration, DeviceManager devices, AmbientPlayer player)
    {
        _client = client;
        _configuration = configuration;
        _devices = devices;
        _player = player;

        player.PlayingChanged += (_, e) => Publish(e.Device.DeviceId, discover: false);
        devices.DeviceRemoved += (_, e) =>
        {
            if (configuration.TopicPrefixes.Contains(e.Device.TopicPrefix))
                Publish(e.Device.DeviceId, discover: true);
        };

        _publishing = Task.Run(PublishAllAsync);
    }

    public static string StateTopic(string deviceId) => $"intercom/server/devices/{deviceId}/state";

    private static string CommandTopic(string deviceId) => $"intercom/server/devices/{deviceId}/set/ambient";

    private static string SwitchTopic(string deviceId) => $"homeassistant/switch/{NodeId}/{deviceId}_ambient_sound/config";

    private static string SensorTopic(string deviceId) => $"homeassistant/sensor/{NodeId}/{deviceId}_ambient_track/config";

    Task IHostedService.StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Publishes what is still to go out, and nothing after it. Hosted services stop in reverse,
    /// so this runs once every playback has ended, and before the server disconnects.
    /// </summary>
    async Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        _publications.Writer.Complete();
        await _publishing.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Publishes the discovery and the state of <paramref name="device"/>, its switch off as the
    /// server has just found it, or as the configuration it re-read says.
    /// </summary>
    public void HandleConfiguration(Device device)
    {
        if (_configuration.TopicPrefixes.Contains(device.TopicPrefix))
            Publish(device.DeviceId, discover: true);
    }

    /// <summary>
    /// Starts or stops the ambient sound on the device <paramref name="deviceId"/>, as the switch's
    /// <c>ON</c> or <c>OFF</c> asks, without waiting for either. A retained command is one the
    /// broker kept from before the server subscribed, and is ignored.
    /// </summary>
    public void HandleCommand(string deviceId, MqttApplicationMessage message)
    {
        var command = message.ConvertPayloadToString();

        if (message.Retain)
        {
            Logger.Information("Ignoring the retained ambient command {Command} for device {Device}", command, deviceId);
            return;
        }

        if (!_devices.TryGet(deviceId, out var device))
        {
            Logger.Warning("Ignoring the ambient command {Command} for device {Device}, which the server does not know", command, deviceId);
            return;
        }

        Logger.Information("Received the ambient command {Command} for device {Device}", command, deviceId);

        switch (command)
        {
            case "ON":
                _ = StartAsync(device);
                break;

            case "OFF":
                _ = _player.StopAsync(device);
                break;

            default:
                Logger.Warning("Ignoring the unknown ambient command {Command} for device {Device}", command, deviceId);
                break;
        }
    }

    private async Task StartAsync(Device device)
    {
        AmbientResult? result = null;
        try
        {
            result = await _player.StartAsync(device, null, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "The switch could not start the ambient sound on device {Device}", device.DeviceId);
        }

        // Home Assistant shows the switch as the state it is sent, so a refused start says it is off.
        if (result?.Track == null)
            Publish(device.DeviceId, discover: false);
    }

    // Once the switch has stopped, at the server's shutdown, the writer refuses what comes after.
    private void Publish(string deviceId, bool discover) => _publications.Writer.TryWrite((deviceId, discover));

    private async Task PublishAllAsync()
    {
        await foreach (var (deviceId, discover) in _publications.Reader.ReadAllAsync())
        {
            try
            {
                await PublishAsync(deviceId, discover);
            }
            catch (Exception ex)
            {
                // A device's next configuration, which the broker sends again at every
                // reconnect, publishes all of it again.
                Logger.Warning(ex, "Could not publish the ambient switch of device {Device}", deviceId);
            }
        }
    }

    private async Task PublishAsync(string deviceId, bool discover)
    {
        var discoverable = Discoverable(deviceId);

        if (discover)
        {
            if (discoverable is not (var device, var uniqueId))
            {
                Logger.Information("Removing the ambient switch of device {Device}", deviceId);
                await PublishRetainedAsync(SwitchTopic(deviceId), "");
                await PublishRetainedAsync(SensorTopic(deviceId), "");
                await PublishRetainedAsync(StateTopic(deviceId), "");
                return;
            }

            await PublishRetainedAsync(SwitchTopic(deviceId), SwitchDiscovery(device, uniqueId).ToJsonString());
            await PublishRetainedAsync(SensorTopic(deviceId), SensorDiscovery(device, uniqueId).ToJsonString());
        }

        if (discoverable is (var playing, _))
        {
            var state = _player.Playing(playing) is { } track
                ? new JsonObject { ["ambient"] = "ON", ["track"] = track }
                : new JsonObject { ["ambient"] = "OFF" };
            await PublishRetainedAsync(StateTopic(deviceId), state.ToJsonString());
        }
    }

    // The device of the id, while it is known and under an ambient prefix, and the unique_id of
    // its configuration, which puts its entities on its page.
    private (Device Device, string UniqueId)? Discoverable(string deviceId)
    {
        if (!_devices.TryGet(deviceId, out var device) || !_configuration.TopicPrefixes.Contains(device.TopicPrefix))
            return null;

        if (device.Configuration?.UniqueId is not { Length: > 0 } uniqueId)
        {
            Logger.Warning("Device {Device} has no unique_id, so it gets no ambient switch", deviceId);
            return null;
        }

        return (device, uniqueId);
    }

    private static JsonObject SwitchDiscovery(Device device, string uniqueId)
    {
        var discovery = Discovery(device, uniqueId, "Ambient sound", "ambient_sound");
        discovery["command_topic"] = CommandTopic(device.DeviceId);
        discovery["payload_on"] = "ON";
        discovery["payload_off"] = "OFF";
        discovery["value_template"] = "{{ value_json.ambient }}";
        return discovery;
    }

    private static JsonObject SensorDiscovery(Device device, string uniqueId)
    {
        var discovery = Discovery(device, uniqueId, "Ambient track", "ambient_track");
        // The off state has no track, and the sensor is then empty.
        discovery["value_template"] = "{{ value_json.track | default('') }}";
        return discovery;
    }

    // What the switch and the sensor share: the device's page, its state topic, and availability
    // while both the server and the device are online.
    private static JsonObject Discovery(Device device, string uniqueId, string name, string objectId) =>
        new()
        {
            ["name"] = name,
            ["unique_id"] = $"{NodeId}_{device.DeviceId}_{objectId}",
            ["state_topic"] = StateTopic(device.DeviceId),
            ["availability"] = new JsonArray(
                new JsonObject
                {
                    ["topic"] = Server.StatusTopic,
                    ["payload_available"] = Server.Online,
                    ["payload_not_available"] = Server.Offline,
                },
                // Read as the firmware's own entities read it. Home Assistant takes a payload
                // setting as text, true as "True", which is how its template renders the state's
                // online; a payload that matches neither setting leaves the availability as it was.
                new JsonObject
                {
                    ["topic"] = $"{device.TopicPrefix}/{device.DeviceId}/state",
                    ["value_template"] = "{{ value_json.online }}",
                    ["payload_available"] = true,
                    ["payload_not_available"] = false,
                }
            ),
            ["availability_mode"] = "all",
            ["device"] = new JsonObject { ["identifiers"] = new JsonArray(uniqueId) },
        };

    private Task PublishRetainedAsync(string topic, string payload) =>
        _client.PublishAsync(
            new MqttApplicationMessageBuilder()
                .WithTopic(topic)
                .WithPayload(payload)
                .WithRetainFlag()
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build()
        );
}
