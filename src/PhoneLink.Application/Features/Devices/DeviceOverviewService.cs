using PhoneLink.Application.Ports;

namespace PhoneLink.Application.Features.Devices;

public sealed class DeviceOverviewService(ITrustedDeviceStore trustedDeviceStore)
{
    public async Task<IReadOnlyList<DeviceOverview>> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var devices = await trustedDeviceStore.ListAsync(cancellationToken).ConfigureAwait(false);

        return devices
            .OrderBy(device => device.TrustState)
            .ThenBy(device => device.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(device => new DeviceOverview(
                device.Id,
                device.DisplayName,
                device.Platform,
                device.TrustState,
                device.PairedAt,
                device.RevokedAt))
            .ToArray();
    }
}

