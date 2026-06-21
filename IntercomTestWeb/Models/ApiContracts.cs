using IntercomServer.Utils;

namespace IntercomTestWeb.Models;

// REST request/response bodies (§6.1). Serialized with IntercomJson (snake_case); single-word
// fields like "volume"/"enabled"/"action" are unaffected by the naming policy.

public sealed record VolumeRequest(double Volume);

public sealed record EnabledRequest(bool Enabled);

public sealed record ActionRequest(string Action);

public sealed record AutoAcceptRequest(bool Enabled);

// GET /api/devices snapshot. The browser primarily builds its state from the /ws/events replay; this
// endpoint is the equivalent read model for debugging and parity with the spec.
public sealed record RealDeviceEntry(string Id, DeviceConfiguration? Config, DeviceState? State);

public sealed record SimDeviceEntry(
    string Id,
    DeviceConfiguration Config,
    DeviceState State,
    DeviceLedAction? LedRed,
    DeviceLedAction? LedGreen
);

public sealed record DevicesSnapshot(
    IReadOnlyList<RealDeviceEntry> RealDevices,
    IReadOnlyList<SimDeviceEntry> SimDevices,
    bool AutoAccept
);
