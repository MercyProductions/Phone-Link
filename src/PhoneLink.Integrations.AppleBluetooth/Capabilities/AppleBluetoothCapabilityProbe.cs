using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using PhoneLink.Integrations.AppleBluetooth.Ancs;

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

        var ancs = await ProbeAncsServiceAsync(pairedLeDevices, cancellationToken).ConfigureAwait(false);
        var pbapReady = await HasRfcommServiceAsync(
            PbapPhonebookAccessServerUuid,
            cancellationToken).ConfigureAwait(false);
        var mapReady = await HasRfcommServiceAsync(
            MapMessageAccessServerUuid,
            cancellationToken).ConfigureAwait(false);

        return
        [
            Report(AppleBluetoothCapability.AncsNotifications, ancs.Notifications, hasAnyPairedDevice, observedAt, ancs.AccessDenied ? "ancs-access-denied" : "ancs"),
            Report(AppleBluetoothCapability.AncsActions, ancs.Actions, hasAnyPairedDevice, observedAt, ancs.AccessDenied ? "ancs-access-denied" : "ancs-actions-control-point"),
            Report(AppleBluetoothCapability.PbapContacts, pbapReady, hasAnyPairedDevice, observedAt, "pbap"),
            Report(AppleBluetoothCapability.PbapCallHistory, pbapReady, hasAnyPairedDevice, observedAt, "pbap-call-history"),
            Report(AppleBluetoothCapability.MapObserveMessages, mapReady, hasAnyPairedDevice, observedAt, "map"),
            Report(AppleBluetoothCapability.MapSendMessages, mapReady, hasAnyPairedDevice, observedAt, "map-send"),
            Report(AppleBluetoothCapability.MapReplyToMessages, mapReady, hasAnyPairedDevice, observedAt, "map-reply"),
        ];
    }

    private static async Task<AncsCharacteristicSupport> ProbeAncsServiceAsync(
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
                try
                {
                    var characteristics = await result.Services[0].GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(cancellationToken);
                    return AncsCharacteristicSupport.Evaluate(characteristics.Status,
                        characteristics.Characteristics.GroupBy(characteristic => characteristic.Uuid)
                            .ToDictionary(group => group.Key, group => group.Aggregate(GattCharacteristicProperties.None,
                                (properties, characteristic) => properties | characteristic.CharacteristicProperties)));
                }
                finally { foreach (var service in result.Services) service.Dispose(); }
            }
            foreach (var service in result.Services)
            {
                service.Dispose();
            }
        }
        return new(false, false, false);
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
