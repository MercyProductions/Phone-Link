using PhoneLink.Domain.Devices;

namespace PhoneLink.Application.Features.Devices;

public sealed record DeviceOverview(
    DeviceId Id,
    string DisplayName,
    DevicePlatform Platform,
    DeviceTrustState TrustState,
    DateTimeOffset PairedAt,
    DateTimeOffset? RevokedAt);

