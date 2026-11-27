namespace IntercomServer;

/// <summary>
/// Where the server finds its devices. Populated from environment variables in
/// <c>Program.cs</c>.
/// </summary>
internal class DevicesConfiguration
{
    /// <summary>
    /// The MQTT topic prefixes devices publish under, as <c>&lt;prefix&gt;/&lt;device_id&gt;/…</c>:
    /// the intercoms' and the ToiletAutomation devices'. The server reads a device's topics under
    /// each, and sends a device its commands under the prefix it found the device under. Set as a
    /// comma-separated list in <c>DEVICES_TOPIC_PREFIXES</c>.
    /// </summary>
    public IReadOnlyList<string> TopicPrefixes { get; init; } = ["intercom/client", "toilet_automation"];

    /// <summary>
    /// Validates the configuration at startup, throwing on a prefix the server cannot subscribe
    /// under, and on one that lies under another, which would leave a device's topic naming two
    /// prefixes.
    /// </summary>
    public void Validate()
    {
        foreach (var prefix in TopicPrefixes)
        {
            if (prefix.Split('/').Any(level => level.Length == 0 || level.IndexOfAny(['+', '#']) >= 0))
                throw new InvalidOperationException(
                    $"DEVICES_TOPIC_PREFIXES holds '{prefix}', which is not a topic prefix: each is "
                        + "one or more topic levels separated by '/', none of them empty or a wildcard."
                );

            if (TopicPrefixes.FirstOrDefault(other => prefix.StartsWith(other + "/")) is { } outer)
                throw new InvalidOperationException(
                    $"DEVICES_TOPIC_PREFIXES holds '{prefix}', which lies under '{outer}': a device's "
                        + "topic would name both."
                );
        }
    }
}
