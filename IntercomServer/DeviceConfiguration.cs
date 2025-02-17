namespace IntercomServer;

internal record DeviceConfiguration(
    string? UniqueId,
    string? Manufacturer,
    string? Model,
    string? Name
);
