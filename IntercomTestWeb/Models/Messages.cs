using IntercomServer.Utils;

namespace IntercomTestWeb.Models;

// Server -> browser push messages for /ws/events (§6.2). Serialized with IntercomJson (snake_case),
// so a message looks like:
//   { "kind": "real", "id": "0x..", "state": { ..DeviceState.. }, "type": "device-state" }
// The reused device DTOs (DeviceConfiguration / DeviceState / DeviceLedAction) keep their native
// wire shape. "kind" is "real" (discovered over MQTT) or "sim" (a simulated device we host).

public sealed record DeviceConfigMessage(string Kind, string Id, DeviceConfiguration Config)
{
    public string Type => "device-config";
}

public sealed record DeviceStateMessage(
    string Kind,
    string Id,
    DeviceState State,
    // Raw LED actions, only sent for simulated devices so the SimDeviceCard can animate blink.
    DeviceLedAction? LedRed = null,
    DeviceLedAction? LedGreen = null
)
{
    public string Type => "device-state";
}

public sealed record DeviceRemovedMessage(string Kind, string Id)
{
    public string Type => "device-removed";
}

public sealed record ServerSettingsMessage(bool AutoAccept)
{
    public string Type => "server-settings";
}

// Reserved for Phase C (the AEC tool). Defined now so the contract is stable; nothing produces it
// in Phase A.
public sealed record AecStatusMessage(string State, bool HasSample)
{
    public string Type => "aec-status";
}
