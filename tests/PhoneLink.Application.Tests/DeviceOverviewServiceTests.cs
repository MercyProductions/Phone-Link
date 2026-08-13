using PhoneLink.Application.Features.Devices;
using PhoneLink.Application.Ports;
using PhoneLink.Domain.Devices;

namespace PhoneLink.Application.Tests;

public sealed class DeviceOverviewServiceTests
{
    [Fact]
    public async Task GetAsyncReturnsStableDisplayOrder()
    {
        var pairedAt = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        var zeta = Create("Zeta", pairedAt);
        var alpha = Create("Alpha", pairedAt.AddMinutes(1));
        var revoked = Create("Former phone", pairedAt.AddMinutes(2))
            .Revoke(pairedAt.AddHours(1));
        var store = new InMemoryTrustedDeviceStore([zeta, revoked, alpha]);
        var service = new DeviceOverviewService(store);

        var result = await service.GetAsync(CancellationToken.None);

        Assert.Collection(
            result,
            item => Assert.Equal("Alpha", item.DisplayName),
            item => Assert.Equal("Zeta", item.DisplayName),
            item => Assert.Equal("Former phone", item.DisplayName));
    }

    private static TrustedDevice Create(string name, DateTimeOffset pairedAt) =>
        TrustedDevice.Create(
            DeviceId.New(),
            name,
            DevicePlatform.Ios,
            new byte[TrustedDevice.IdentityPublicKeyLength],
            pairedAt);

    private sealed class InMemoryTrustedDeviceStore(IReadOnlyList<TrustedDevice> devices)
        : ITrustedDeviceStore
    {
        public Task<TrustedDevice?> FindAsync(DeviceId id, CancellationToken cancellationToken) =>
            Task.FromResult(devices.SingleOrDefault(device => device.Id == id));

        public Task<IReadOnlyList<TrustedDevice>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult(devices);

        public Task SaveAsync(TrustedDevice device, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RevokeAsync(
            DeviceId id,
            DateTimeOffset revokedAt,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
