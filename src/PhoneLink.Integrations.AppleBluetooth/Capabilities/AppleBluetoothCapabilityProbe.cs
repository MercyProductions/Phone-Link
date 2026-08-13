using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;

namespace PhoneLink.Integrations.AppleBluetooth.Capabilities;

public static class AppleBluetoothCapabilityProbe
{
    private static readonly Guid AncsServiceUuid =
        Guid.Parse("7905f431-b5ce-4e99-a40f-4b1e122d00d0");
    private static readonly Guid PbapPhonebookAccessServerUuid =
        Guid.Parse("0000112f-0000-1000-8000-00805f9b34fb");
    private static readonly Guid MapMessageAccessServerUuid =
        Guid.Parse("00001132-0000-1000-8000-00805f9b34fb");

    public static async Task<IReadOnlyList<AppleBluetoothCapabilityReport>> ProbeAsync(
        CancellationToken cancellationToken)
    {
        var observedAt = DateTimeOffset.UtcNow;
        var pairedLeDevices = await DeviceInformation.FindAllAsync(
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)).AsTask(cancellationToken);
        var pairedClassicDevices = await DeviceInformation.FindAllAsync(
            BluetoothDevice.GetDeviceSelectorFromPairingState(true)).AsTask(cancellationToken);
        var hasAnyPairedDevice = pairedLeDevices.Count > 0 || pairedClassicDevices.Count > 0;

        var ancsReady = await HasAncsServiceAsync(pairedLeDevices, cancellationToken).ConfigureAwait(false);
        var pbapReady = await HasRfcommServiceAsync(
            PbapPhonebookAccessServerUuid,
            cancellationToken).ConfigureAwait(false);
        var mapReady = await HasRfcommServiceAsync(
            MapMessageAccessServerUuid,
            cancellationToken).ConfigureAwait(false);

        return
        [
            Report(AppleBluetoothCapability.AncsNotifications, ancsReady, hasAnyPairedDevice, observedAt, "ancs"),
            Report(AppleBluetoothCapability.AncsActions, ancsReady, hasAnyPairedDevice, observedAt, "ancs-actions"),
            Report(AppleBluetoothCapability.PbapContacts, pbapReady, hasAnyPairedDevice, observedAt, "pbap"),
            Report(AppleBluetoothCapability.PbapCallHistory, pbapReady, hasAnyPairedDevice, observedAt, "pbap-call-history"),
            Report(AppleBluetoothCapability.MapObserveMessages, mapReady, hasAnyPairedDevice, observedAt, "map"),
            Report(AppleBluetoothCapability.MapSendMessages, mapReady, hasAnyPairedDevice, observedAt, "map-send"),
            Report(AppleBluetoothCapability.MapReplyToMessages, mapReady, hasAnyPairedDevice, observedAt, "map-reply"),
        ];
    }

    private static async Task<bool> HasAncsServiceAsync(
        DeviceInformationCollection pairedDevices,
        CancellationToken cancellationToken)
    {
        foreach (var deviceInformation in pairedDevices)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var device = await BluetoothLEDevice
                .FromIdAsync(deviceInformation.Id)
                .AsTask(cancellationToken);
            if (device is null)
            {
                continue;
            }

            var result = await device
                .GetGattServicesForUuidAsync(AncsServiceUuid, BluetoothCacheMode.Uncached)
                .AsTask(cancellationToken);
            if (result.Status == GattCommunicationStatus.Success && result.Services.Count > 0)
            {
                foreach (var service in result.Services)
                {
                    service.Dispose();
                }
                return true;
            }
            foreach (var service in result.Services)
            {
                service.Dispose();
            }
        }
        return false;
    }

    private static async Task<bool> HasRfcommServiceAsync(
        Guid serviceUuid,
        CancellationToken cancellationToken)
    {
        var selector = RfcommDeviceService.GetDeviceSelector(RfcommServiceId.FromUuid(serviceUuid));
        var services = await DeviceInformation.FindAllAsync(selector).AsTask(cancellationToken);
        return services.Count > 0;
    }

    private static AppleBluetoothCapabilityReport Report(
        AppleBluetoothCapability capability,
        bool ready,
        bool hasAnyPairedDevice,
        DateTimeOffset observedAt,
        string source)
    {
        var status = ready
            ? AppleBluetoothCapabilityStatus.Ready
            : hasAnyPairedDevice
                ? AppleBluetoothCapabilityStatus.ReservedByOperatingSystem
                : AppleBluetoothCapabilityStatus.RequiresSystemPairing;
        var reason = ready
            ? $"{source}.service-discovered"
            : hasAnyPairedDevice
                ? $"{source}.not-exposed-to-app"
                : "bluetooth.system-pairing-required";
        return new AppleBluetoothCapabilityReport(capability, status, observedAt, reason);
    }
}
