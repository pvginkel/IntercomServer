namespace IntercomTestWeb.Models;

// Persisted shape of a simulated device (Devices.json), ported from IntercomTest. Phase A only uses
// DeviceId; RecordingDevice / PlaybackDevice are the browser audio-device labels chosen on the
// SimDeviceCard and are reintroduced in Phase B.
public sealed record IntercomClientConfiguration(
    string DeviceId,
    string? RecordingDevice,
    string? PlaybackDevice
);
