using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace IntercomServer;

internal static class NetworkUtils
{
    /// <summary>
    /// Returns the usable IPv4 addresses of this host, ignoring loopback,
    /// link-local (169.254.x.x) and Hyper-V virtual switch adapters.
    /// </summary>
    public static IEnumerable<IPAddress> GetNetworkIPAddresses()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (
                !ni.Supports(NetworkInterfaceComponent.IPv4)
                || ni.Name.Contains("vEthernet")
                || ni.NetworkInterfaceType
                    is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)
                || ni.OperationalStatus != OperationalStatus.Up
            )
                continue;

            var ipProperties = ni.GetIPProperties();

            foreach (var unicast in ipProperties.UnicastAddresses)
            {
                if (
                    unicast.Address.AddressFamily == AddressFamily.InterNetwork
                    && !unicast.Address.ToString().StartsWith("169.254.")
                )
                    yield return unicast.Address;
            }
        }
    }
}
