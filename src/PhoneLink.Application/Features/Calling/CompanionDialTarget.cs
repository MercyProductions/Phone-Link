using PhoneLink.Domain.Devices;

namespace PhoneLink.Application.Features.Calling;

/// <summary>A user-selectable snapshot, never a substitute for live authorization.</summary>
public sealed record CompanionDialTarget(DeviceId DeviceId, Guid SessionId, string DisplayName)
{
    public bool Matches(TrustedDevice? device, Guid activeSessionId) =>
        DeviceId.Value != Guid.Empty && SessionId != Guid.Empty &&
        device is not null && device.Id == DeviceId &&
        device.Platform == DevicePlatform.Ios && device.TrustState == DeviceTrustState.Trusted &&
        activeSessionId == SessionId;

    // Include the paired identity so identically named phones remain distinguishable.
    public string SelectionLabel => $"{DisplayName} — {DeviceId}";
}
