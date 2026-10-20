using PhoneLink.DeviceCommunication.Discovery;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class BonjourServiceObservationTests
{
    [Fact]
    public void AdvertisementNeverProvesIdentityAndUntrustedMetadataIsBounded()
    {
        var properties = new Dictionary<string, object>
        {
            ["System.Devices.Dnssd.ServiceName"] = "_airplay._tcp",
            ["System.Devices.Dnssd.InstanceName"] = "iPhone\n\u202eevil" + new string('x', 300),
            ["System.Devices.Dnssd.HostName"] = "phone.local",
            ["System.Devices.Dnssd.PortNumber"] = (ushort)7000,
            ["System.Devices.IpAddress"] = new[] { "192.168.1.2", "192.168.1.2", "not-an-address", "fd00::2" },
        };
        var result = BonjourServiceObservation.FromProperties("hint", "fallback", properties, DateTimeOffset.UtcNow);
        Assert.NotNull(result);
        Assert.False(result.IsIdentityVerified);
        Assert.Equal(200, result.DisplayName.Length);
        Assert.DoesNotContain('\n', result.DisplayName);
        Assert.DoesNotContain('\u202e', result.DisplayName);
        Assert.Equal(7000, result.Port);
        Assert.Equal(2, result.Addresses.Count);
    }

    [Fact]
    public void RejectsUnknownServiceAndDoesNotInventMissingEndpoint()
    {
        Assert.Null(BonjourServiceObservation.FromProperties("id", "name",
            new Dictionary<string, object>(), DateTimeOffset.UtcNow));
        var result = BonjourServiceObservation.FromProperties("id", "name",
            new Dictionary<string, object> { ["System.Devices.Dnssd.ServiceName"] = "_phonelink._tcp" },
            DateTimeOffset.UtcNow);
        Assert.NotNull(result);
        Assert.Equal("name", result.DisplayName);
        Assert.Equal("", result.HostName);
        Assert.Null(result.Port);
        Assert.Empty(result.Addresses);
    }

    [Fact]
    public async Task CancellationBeforeDiscoveryStartsDoesNotStartWatchers()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WindowsBonjourBrowser.DiscoverAsync(TimeSpan.FromSeconds(1), cancellation.Token));
    }
}
