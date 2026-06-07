using IntercomServer.Utils;

namespace IntercomServer;

/// <summary>
/// Resolves the audio endpoint (host:port) that devices stream to while in a conversation.
/// The host comes from <see cref="AudioServerConfiguration.Host"/> when set, otherwise it
/// is auto-detected from this host's LAN IPv4 addresses. The port is the one the shared
/// <see cref="UdpAudioServer"/> is actually bound to.
/// </summary>
internal sealed class AudioEndpointResolver(
    AudioServerConfiguration configuration,
    UdpAudioServer audioServer
)
{
    private string? _endpoint;

    /// <summary>
    /// The endpoint devices must be able to reach. Behind NAT or a Kubernetes
    /// LoadBalancer, <c>AUDIO_HOST</c> must be set to the external/LB address, because
    /// auto-detection returns this host's own NIC address (the pod IP inside Kubernetes,
    /// which devices cannot reach).
    /// </summary>
    public string Endpoint => _endpoint ??= $"{ResolveHost()}:{audioServer.LocalEndPoint.Port}";

    private string ResolveHost()
    {
        if (!string.IsNullOrEmpty(configuration.Host))
            return configuration.Host;

        var address = NetworkUtils.GetNetworkIPAddresses().FirstOrDefault();
        if (address == null)
        {
            throw new InvalidOperationException(
                "Could not auto-detect a LAN IP address for the audio endpoint. "
                    + "Set the AUDIO_HOST environment variable."
            );
        }

        return address.ToString();
    }
}
