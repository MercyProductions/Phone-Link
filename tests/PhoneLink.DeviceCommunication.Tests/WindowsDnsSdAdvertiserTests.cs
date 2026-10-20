using PhoneLink.DeviceCommunication.Discovery;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class WindowsDnsSdAdvertiserTests
{
    [Fact]
    public async Task PreCancelledRegistrationDoesNotStartNativeWork()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WindowsDnsSdAdvertiser.StartAsync("Cancelled Phone Link", 54_321, cancellation.Token));
    }

    [Fact]
    public async Task ConcurrentDisposalSharesOneNativeDeregistration()
    {
        var advertiser = await WindowsDnsSdAdvertiser.StartAsync(
            $"Phone Link Test {Guid.NewGuid():N}", 54_321, CancellationToken.None);
        await Task.WhenAll(advertiser.DisposeAsync().AsTask(), advertiser.DisposeAsync().AsTask());
        await advertiser.DisposeAsync();
    }

    [Fact]
    public async Task RegistersAndDeregistersProcessScopedBonjourService()
    {
        await using var advertiser = await WindowsDnsSdAdvertiser.StartAsync(
            $"Phone Link Test {Guid.NewGuid():N}",
            54_321,
            CancellationToken.None);
    }

    [Fact]
    public async Task WindowsBrowserFindsRegisteredServiceWithoutTrustingIt()
    {
        var name = $"Phone Link Test {Guid.NewGuid():N}";
        await using var advertiser = await WindowsDnsSdAdvertiser.StartAsync(name, 54_321, CancellationToken.None);
        var services = await WindowsBonjourBrowser.DiscoverAsync(TimeSpan.FromSeconds(4), CancellationToken.None);
        var observation = Assert.Single(services, service => service.DisplayName == name);
        Assert.Equal("_phonelink._tcp", observation.ServiceType);
        Assert.Equal(54_321, observation.Port);
        Assert.False(observation.IsIdentityVerified);
    }

    [Fact]
    public async Task ActiveBrowserCancellationStopsWatchersAndAllowsAnotherScan()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            WindowsBonjourBrowser.DiscoverAsync(TimeSpan.FromSeconds(10), cancellation.Token));
        // A fresh native scan must still be usable after the previous watchers stop.
        var name = $"Phone Link Test {Guid.NewGuid():N}";
        await using var advertiser = await WindowsDnsSdAdvertiser.StartAsync(name, 54_322, CancellationToken.None);
        var services = await WindowsBonjourBrowser.DiscoverAsync(TimeSpan.FromSeconds(4), CancellationToken.None);
        Assert.Contains(services, service => service.DisplayName == name && service.Port == 54_322);
    }
}
