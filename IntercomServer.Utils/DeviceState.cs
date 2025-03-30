namespace IntercomServer.Utils;

public record DeviceState(
    bool? Online,
    bool? Enabled,
    bool? RedLed,
    bool? GreenLed,
    bool? Playing,
    bool? Recording
);
