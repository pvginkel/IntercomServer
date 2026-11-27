namespace IntercomServer;

/// <summary>
/// Which devices the doorbell rings besides those that can take a call. Populated from
/// environment variables in <c>Program.cs</c>.
/// </summary>
internal class DoorbellConfiguration
{
    /// <summary>
    /// The ids of the devices the doorbell also rings, such as a ToiletAutomation device, which has
    /// no microphone. A listed device is rung while it is online, enabled or not, as
    /// <c>enabled</c> is an intercom's flag. Set as a comma-separated list in
    /// <c>DOORBELL_EXTRA_DEVICES</c>.
    /// </summary>
    public IReadOnlyList<string> ExtraDevices { get; init; } = [];

    /// <summary>
    /// Validates the configuration at startup, throwing on an entry that cannot be a device id,
    /// which is one topic level.
    /// </summary>
    public void Validate()
    {
        foreach (var deviceId in ExtraDevices)
        {
            if (deviceId.Length == 0 || deviceId.IndexOfAny(['/', '+', '#']) >= 0)
                throw new InvalidOperationException(
                    $"DOORBELL_EXTRA_DEVICES holds '{deviceId}', which is not a device id: each is "
                        + "one topic level, neither empty nor holding '/', '+' or '#'."
                );
        }
    }
}
