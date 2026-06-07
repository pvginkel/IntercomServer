namespace IntercomServer;

/// <summary>
/// Configuration for the server's UDP audio endpoint — the address devices stream audio
/// to (and the port the server listens on). This is a global server concern, independent
/// of the ChatGPT feature.
/// </summary>
internal class AudioServerConfiguration
{
    /// <summary>UDP port the server listens on for inbound device audio.</summary>
    public int Port { get; init; } = 5004;

    /// <summary>
    /// Host/IP that devices should stream audio to. This must be reachable by the devices,
    /// so behind NAT or a Kubernetes LoadBalancer it must be set to the external/LB address.
    /// When empty, the first usable LAN IPv4 address of this host is auto-detected (which is
    /// not valid inside a Kubernetes pod).
    /// </summary>
    public string? Host { get; init; }
}
