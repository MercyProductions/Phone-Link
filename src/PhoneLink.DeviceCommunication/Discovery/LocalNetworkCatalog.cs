using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using PhoneLink.Protocol.Pairing;

namespace PhoneLink.DeviceCommunication.Discovery;

public sealed record LocalNetworkInterface(
    string Id,
    string Name,
    NetworkInterfaceType Type,
    OperationalStatus Status,
    IReadOnlyList<IPAddress> Addresses);

public static class LocalNetworkCatalog
{
    public static IReadOnlyList<LocalNetworkInterface> Capture() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(network => network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(network => new LocalNetworkInterface(
                network.Id,
                network.Name,
                network.NetworkInterfaceType,
                network.OperationalStatus,
                network.GetIPProperties().UnicastAddresses
                    .Select(address => address.Address)
                    .Where(address => !IPAddress.IsLoopback(address))
                    .Distinct()
                    .ToArray()))
            .OrderBy(network => network.Status == OperationalStatus.Up ? 0 : 1)
            .ThenBy(network => network.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IPAddress[] SelectPairingAddresses(IEnumerable<LocalNetworkInterface> networks)
    {
        ArgumentNullException.ThrowIfNull(networks);
        return networks
            .Where(network => network.Status == OperationalStatus.Up)
            .SelectMany(network => network.Addresses)
            .Where(address => !IPAddress.IsLoopback(address))
            .Where(address => PairingOfferValidator.IsLocalEndpoint(address.ToString()))
            // A Windows interface scope ID is not valid on the receiving iPhone.
            // Bonjour resolves link-local IPv6 on the receiving interface instead.
            .Where(address => !address.IsIPv6LinkLocal)
            .Distinct()
            .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ThenBy(address => address.ToString(), StringComparer.Ordinal)
            .Take(8)
            .ToArray();
    }
}
