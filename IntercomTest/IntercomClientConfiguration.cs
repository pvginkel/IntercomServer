namespace IntercomTest;

internal record IntercomClientConfiguration(
    string DeviceId,
    string? RecordingDevice,
    string? PlaybackDevice
);
