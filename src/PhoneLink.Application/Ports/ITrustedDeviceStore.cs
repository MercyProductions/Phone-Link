using PhoneLink.Domain.Devices;

namespace PhoneLink.Application.Ports;

public interface ITrustedDeviceStore
{
    Task<TrustedDevice?> FindAsync(DeviceId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<TrustedDevice>> ListAsync(CancellationToken cancellationToken);

    Task SaveAsync(TrustedDevice device, CancellationToken cancellationToken);

    Task RevokeAsync(
        DeviceId id,
        DateTimeOffset revokedAt,
        CancellationToken cancellationToken);
}

