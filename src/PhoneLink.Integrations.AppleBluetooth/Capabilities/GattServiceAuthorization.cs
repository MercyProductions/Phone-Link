using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;

namespace PhoneLink.Integrations.AppleBluetooth.Capabilities;

internal static class GattServiceAuthorization
{
    internal static async Task<DeviceAccessStatus> ReadOrRequestForManualStartAsync(GattDeviceService service, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var current = service.DeviceAccessInformation.CurrentStatus;
        token.ThrowIfCancellationRequested();
        return ShouldRequest(current) ? await service.RequestAccessAsync().AsTask(token) : current;
    }

    internal static bool ShouldRequest(DeviceAccessStatus current) => current == DeviceAccessStatus.Unspecified;
    internal static bool IsSharedOpen(GattOpenStatus result) => result is GattOpenStatus.Success or GattOpenStatus.AlreadyOpened;
}
