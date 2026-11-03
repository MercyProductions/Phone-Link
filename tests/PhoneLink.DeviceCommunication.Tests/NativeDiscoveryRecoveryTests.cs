using System.Net;
using System.Net.Sockets;
using PhoneLink.Application.Features.Diagnostics;
using PhoneLink.DeviceCommunication.Discovery;
using PhoneLink.Domain.Devices;
using PhoneLink.Security.Pairing;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class NativeDiscoveryRecoveryTests
{
    [Fact]
    public async Task MissingNativeAdvertisementIsRecoveredObservedAndNotRestartedWhenHealthy()
    {
        // Only fixture identity/trust in memory. Never add a peer to the owner's app database.
        using var identity = Ed25519Identity.Create();
        var peer = TrustedDevice.Create(DeviceId.New(), "Synthetic recovery peer", DevicePlatform.Ios,
            identity.ExportPublicKey(), DateTimeOffset.UtcNow);
        var instance = $"PL Recovery Test {Guid.NewGuid():N}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        WindowsDnsSdAdvertiser? advertiser = null;
        var registerCalls = 0;

        Task<DiscoveryRecoverySnapshot> Capture(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(new DiscoveryRecoverySnapshot(false,
                peer.TrustState == DeviceTrustState.Trusted ? 1 : 0,
                LocalNetworkCatalog.SelectPairingAddresses(LocalNetworkCatalog.Capture()).Length,
                advertiser?.IsRegistered == true));
        }
        async Task Register(CancellationToken token)
        {
            registerCalls++;
            advertiser = await WindowsDnsSdAdvertiser.StartAsync(instance, port, token);
        }

        try
        {
            var before = await Capture(timeout.Token);
            Assert.True(before.EligibleAddresses > 0, "Native recovery validation requires an eligible local interface.");
            Assert.False(before.AdvertisementRegistered);
            var beforeBrowse = await WindowsBonjourBrowser.DiscoverAsync(TimeSpan.FromSeconds(4), timeout.Token);
            Assert.DoesNotContain(beforeBrowse, service => service.DisplayName == instance);

            var recovered = await DiscoveryRecovery.RunAsync(Capture, Register, timeout.Token);
            Assert.Equal(DiscoveryRecoveryOutcome.RecoveredLocally, recovered.Outcome);
            Assert.True(recovered.ActionAttempted);
            Assert.False(recovered.Before.AdvertisementRegistered);
            Assert.True(recovered.After!.AdvertisementRegistered);
            Assert.Equal(1, registerCalls);

            var afterBrowse = await WindowsBonjourBrowser.DiscoverAsync(TimeSpan.FromSeconds(4), timeout.Token);
            var observed = Assert.Single(afterBrowse, service => service.DisplayName == instance && service.Port == port);
            Assert.Equal("_phonelink._tcp", observed.ServiceType);
            Assert.False(observed.IsIdentityVerified);

            var healthyAdvertiser = advertiser;
            var healthy = await DiscoveryRecovery.RunAsync(Capture, Register, timeout.Token);
            Assert.Equal(DiscoveryRecoveryOutcome.NoActionRequired, healthy.Outcome);
            Assert.False(healthy.ActionAttempted);
            Assert.Same(healthyAdvertiser, advertiser);
            Assert.Equal(1, registerCalls);

            // Deliberately remove only this fixture's native registration, then run the same policy again.
            await advertiser!.DisposeAsync();
            Assert.False(advertiser.IsRegistered);
            advertiser = null;
            var recoveredAgain = await DiscoveryRecovery.RunAsync(Capture, Register, timeout.Token);
            Assert.Equal(DiscoveryRecoveryOutcome.RecoveredLocally, recoveredAgain.Outcome);
            Assert.True(recoveredAgain.ActionAttempted);
            Assert.False(recoveredAgain.Before.AdvertisementRegistered);
            Assert.True(recoveredAgain.After!.AdvertisementRegistered);
            Assert.Equal(2, registerCalls);
            Assert.NotSame(healthyAdvertiser, advertiser);
        }
        finally
        {
            if (advertiser is not null) await advertiser.DisposeAsync();
            listener.Stop();
        }
        Assert.False(advertiser?.IsRegistered);
    }
}
