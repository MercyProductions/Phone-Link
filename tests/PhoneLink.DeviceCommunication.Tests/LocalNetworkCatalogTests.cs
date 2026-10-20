using System.Net;
using System.Net.NetworkInformation;
using PhoneLink.DeviceCommunication.Discovery;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class LocalNetworkCatalogTests
{
    private static readonly string[] ExpectedAddresses = ["10.0.0.20", "192.168.1.20", "fd12::20"];

    [Fact]
    public void PairingUsesOnlyActivePrivateAddressesAndDeduplicatesInterfaces()
    {
        var addresses = LocalNetworkCatalog.SelectPairingAddresses([
            Network("wifi", OperationalStatus.Up, "192.168.1.20", "fd12::20", "8.8.8.8", "127.0.0.1", "fe80::20%12"),
            Network("vpn", OperationalStatus.Up, "192.168.1.20", "10.0.0.20"),
            Network("offline", OperationalStatus.Down, "172.16.0.20"),
        ]);

        Assert.Equal(ExpectedAddresses,
            addresses.Select(address => address.ToString()));
    }

    [Fact]
    public void PairingCapsAddressesAndReturnsNothingWhenAllInterfacesAreDown()
    {
        var many = Enumerable.Range(1, 20).Select(value => $"10.0.0.{value}").ToArray();
        Assert.Equal(8, LocalNetworkCatalog.SelectPairingAddresses([
            Network("wifi", OperationalStatus.Up, many),
        ]).Length);
        Assert.Empty(LocalNetworkCatalog.SelectPairingAddresses([
            Network("wifi", OperationalStatus.Down, many),
        ]));
    }

    private static LocalNetworkInterface Network(string name, OperationalStatus status, params string[] addresses) =>
        new(name, name, NetworkInterfaceType.Wireless80211, status, addresses.Select(IPAddress.Parse).ToArray());
}
