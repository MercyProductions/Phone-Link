using PhoneLink.Integrations.AppleBluetooth.Capabilities;
using Windows.Devices.Enumeration;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class AppleGattAccessProbeTests
{
    [Theory]
    [InlineData(DeviceAccessStatus.Allowed, true)]
    [InlineData(DeviceAccessStatus.Unspecified, false)]
    [InlineData(DeviceAccessStatus.DeniedByUser, false)]
    [InlineData(DeviceAccessStatus.DeniedBySystem, false)]
    [InlineData(null, false)]
    public void OnlyAnExistingAllowancePermitsTheNextStage(DeviceAccessStatus? status, bool allowed) =>
        Assert.Equal(allowed, AppleGattAccessProbe.CanContinue(status));

    [Theory]
    [InlineData(DeviceAccessStatus.Allowed, false)]
    [InlineData(DeviceAccessStatus.Unspecified, true)]
    [InlineData(DeviceAccessStatus.DeniedByUser, false)]
    [InlineData(DeviceAccessStatus.DeniedBySystem, false)]
    public void ManualStartupOnlyRequestsAccessForAnUnspecifiedDecision(DeviceAccessStatus status, bool requests) =>
        Assert.Equal(requests, GattServiceAuthorization.ShouldRequest(status));

    [Fact]
    public void InventorySeparatesDenialFromAnUnknownOrUnavailableStatus()
    {
        var summary = AppleGattAccessProbe.CountStatuses([DeviceAccessStatus.Allowed, DeviceAccessStatus.DeniedByUser,
            DeviceAccessStatus.DeniedBySystem, DeviceAccessStatus.Unspecified, null]);
        Assert.Equal(new BluetoothEndpointAccessInventory(5, 1, 1, 1, 2), summary);
    }

    [Fact]
    public void ReportSeparatesDeniedCharacteristicsFromUnattemptedAndSuccessfulEmptyDiscovery()
    {
        var report = new AppleGattAccessReport("Allowed", "Paired device opened for inspection",
        [
            new("Notifications (ANCS)", "Success", 1, "Allowed", "Success", "AccessDenied"),
            new("Media (AMS)", "Success", 0),
        ]).ToDisplayText();
        Assert.Contains("characteristic enumeration: AccessDenied.", report);
        Assert.Contains("Media (AMS) — discovery: Success (0); existing service access: Not attempted", report);
        Assert.Contains("does not verify working notifications", report);
    }

    [Fact]
    public async Task PreCancelledInspectionDoesNotOpenTheSelectedDevice()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AppleGattAccessProbe.InspectAsync(new("unused-fixture-id", "Fixture"), cancellation.Token));
    }
}
