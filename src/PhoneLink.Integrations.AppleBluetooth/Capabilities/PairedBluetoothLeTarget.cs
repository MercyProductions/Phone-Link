using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace PhoneLink.Integrations.AppleBluetooth.Capabilities;

public sealed class PairedBluetoothLeTarget
{
    internal PairedBluetoothLeTarget(string id, string displayName) => (Id, DisplayName) = (id, displayName);
    internal string Id { get; }
    public string DisplayName { get; }
    public override string ToString() => DisplayName;

    // Enumeration does not perform GATT discovery or open a profile.
    public static async Task<IReadOnlyList<PairedBluetoothLeTarget>> ListAsync(CancellationToken cancellationToken)
    {
        var devices = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true))
            .AsTask(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (devices.Count > 32) throw new InvalidOperationException("The paired Bluetooth LE device list exceeds the inspection limit.");
        return devices.Select(device => new PairedBluetoothLeTarget(device.Id,
            string.IsNullOrWhiteSpace(device.Name) ? "Unnamed paired Bluetooth LE device" : device.Name)).ToArray();
    }
}
