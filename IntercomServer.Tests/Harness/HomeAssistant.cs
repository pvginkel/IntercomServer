using System.Text.Json.Nodes;

namespace IntercomServer.Tests.Harness;

/// <summary>
/// The ambient switch as Home Assistant meets it: the topics the server publishes its discovery,
/// its state and its status on, and the one the switch's commands go to. They and the JSON on
/// them are written out here, field by field, because they are Home Assistant's wire contract.
/// </summary>
internal static class HomeAssistant
{
    public const string StatusTopic = "intercom/server/status";

    public static string StateTopic(string deviceId) => $"intercom/server/devices/{deviceId}/state";

    public static string CommandTopic(string deviceId) => $"intercom/server/devices/{deviceId}/set/ambient";

    public static string SwitchTopic(string deviceId) => $"homeassistant/switch/intercom_server/{deviceId}_ambient_sound/config";

    public static string SensorTopic(string deviceId) => $"homeassistant/sensor/intercom_server/{deviceId}_ambient_track/config";

    /// <summary>The state of a device whose ambient sound is off: no track, and the sensor empty.</summary>
    public static JsonObject Off() => new() { ["ambient"] = "OFF" };

    public static JsonObject On(string track) => new() { ["ambient"] = "ON", ["track"] = track };

    /// <summary>Turns the device's switch <paramref name="command"/>, <c>ON</c> or <c>OFF</c>, as Home Assistant does.</summary>
    public static Task SwitchAsync(this ServerHarness harness, string deviceId, string command, bool retain = false) =>
        harness.PublishAsync(CommandTopic(deviceId), command, retain);

    /// <summary>The discovery of the device's "Ambient sound" switch, on the page of <paramref name="uniqueId"/>.</summary>
    public static JsonObject SwitchDiscovery(TestDevice device, string uniqueId)
    {
        var discovery = Discovery(device, uniqueId, "Ambient sound", "ambient_sound");
        discovery["command_topic"] = CommandTopic(device.DeviceId);
        discovery["payload_on"] = "ON";
        discovery["payload_off"] = "OFF";
        discovery["value_template"] = "{{ value_json.ambient }}";
        return discovery;
    }

    /// <summary>The discovery of the device's "Ambient track" sensor, on the page of <paramref name="uniqueId"/>.</summary>
    public static JsonObject SensorDiscovery(TestDevice device, string uniqueId)
    {
        var discovery = Discovery(device, uniqueId, "Ambient track", "ambient_track");
        discovery["value_template"] = "{{ value_json.track | default('') }}";
        return discovery;
    }

    // The device's availability reads its state as the firmware's own entities read it,
    // github:pvginkel/esp-libs esp-mqtt-support/src/MQTTConnection.cpp: the template, true as the
    // payload available, and every side's availability together. False, as the payload not
    // available, is the server's own: Home Assistant ignores an availability payload that
    // matches neither.
    private static JsonObject Discovery(TestDevice device, string uniqueId, string name, string objectId) =>
        new()
        {
            ["name"] = name,
            ["unique_id"] = $"intercom_server_{device.DeviceId}_{objectId}",
            ["state_topic"] = StateTopic(device.DeviceId),
            ["availability"] = new JsonArray(
                new JsonObject
                {
                    ["topic"] = StatusTopic,
                    ["payload_available"] = "online",
                    ["payload_not_available"] = "offline",
                },
                new JsonObject
                {
                    ["topic"] = $"{device.Prefix}/{device.DeviceId}/state",
                    ["value_template"] = "{{ value_json.online }}",
                    ["payload_available"] = true,
                    ["payload_not_available"] = false,
                }
            ),
            ["availability_mode"] = "all",
            ["device"] = new JsonObject { ["identifiers"] = new JsonArray(uniqueId) },
        };
}
