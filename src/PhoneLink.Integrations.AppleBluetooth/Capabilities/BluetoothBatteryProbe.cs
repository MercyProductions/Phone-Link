using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace PhoneLink.Integrations.AppleBluetooth.Capabilities;

public sealed class PairedBatteryTarget
{
    internal PairedBatteryTarget(string id, string displayName) => (Id, DisplayName) = (id, displayName);
    internal string Id { get; }
    public string DisplayName { get; }
    public override string ToString() => DisplayName;
}

public enum BatteryReadOutcome { Reported, NotExposed, AccessDenied, Unreachable, ProtocolError, InvalidValue, Ambiguous, Unavailable }
public enum BatteryReadStage { Device, Service, Characteristic, Value }
public sealed record BluetoothBatteryReading(BatteryReadStage Stage, BatteryReadOutcome Outcome, int? Percent = null);

public static class BluetoothBatteryProbe
{
    private const int MaximumServices = 4;

    // Enumeration alone does not perform GATT discovery or request battery data.
    public static async Task<IReadOnlyList<PairedBatteryTarget>> ListPairedAsync(CancellationToken cancellationToken)
    {
        var devices = await DeviceInformation.FindAllAsync(BluetoothLEDevice.GetDeviceSelectorFromPairingState(true))
            .AsTask(cancellationToken).ConfigureAwait(false);
        if (devices.Count > 32) throw new InvalidOperationException("The paired Bluetooth LE device list exceeds the inspection limit.");
        return devices.Select(device => new PairedBatteryTarget(device.Id,
            string.IsNullOrWhiteSpace(device.Name) ? "Unnamed paired Bluetooth LE device" : device.Name)).ToArray();
    }

    public static async Task<IReadOnlyList<BluetoothBatteryReading>> ReadAsync(PairedBatteryTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));
        var token = deadline.Token;
        var stage = BatteryReadStage.Device;
        try
        {
            using var device = await BluetoothLEDevice.FromIdAsync(target.Id).AsTask(token).ConfigureAwait(false);
            if (device is null || !device.DeviceInformation.Pairing.IsPaired)
                return [new(stage, BatteryReadOutcome.Unavailable)];

            stage = BatteryReadStage.Service;
            var services = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached)
                .AsTask(token).ConfigureAwait(false);
            try
            {
                if (services.Status != GattCommunicationStatus.Success) return [Failure(stage, services.Status)];
                if (services.Services.Count == 0) return [new(stage, BatteryReadOutcome.NotExposed)];
                if (services.Services.Count > MaximumServices) return [new(stage, BatteryReadOutcome.Ambiguous)];
                List<BluetoothBatteryReading> readings = [];
                foreach (var service in services.Services)
                {
                    stage = BatteryReadStage.Characteristic;
                    var characteristics = await service.GetCharacteristicsForUuidAsync(GattCharacteristicUuids.BatteryLevel,
                        BluetoothCacheMode.Uncached).AsTask(token).ConfigureAwait(false);
                    if (characteristics.Status != GattCommunicationStatus.Success) readings.Add(Failure(stage, characteristics.Status));
                    else if (characteristics.Characteristics.Count == 0) readings.Add(new(stage, BatteryReadOutcome.NotExposed));
                    else if (characteristics.Characteristics.Count != 1) readings.Add(new(stage, BatteryReadOutcome.Ambiguous));
                    else
                    {
                        var characteristic = characteristics.Characteristics[0];
                        if ((characteristic.CharacteristicProperties & GattCharacteristicProperties.Read) == 0)
                        {
                            readings.Add(new(stage, BatteryReadOutcome.NotExposed));
                            continue;
                        }
                        stage = BatteryReadStage.Value;
                        var value = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(token).ConfigureAwait(false);
                        if (value.Status != GattCommunicationStatus.Success) readings.Add(Failure(stage, value.Status));
                        else if (value.Value is null || value.Value.Length != 1) readings.Add(Decode([]));
                        else
                        {
                            using var reader = DataReader.FromBuffer(value.Value);
                            readings.Add(Decode([reader.ReadByte()]));
                        }
                    }
                }
                token.ThrowIfCancellationRequested();
                return readings.AsReadOnly();
            }
            finally { foreach (var service in services.Services) service.Dispose(); }
        }
        catch (UnauthorizedAccessException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return [new(stage, BatteryReadOutcome.AccessDenied)];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return [new(stage, BatteryReadOutcome.Unavailable)];
        }
    }

    internal static BluetoothBatteryReading Decode(ReadOnlySpan<byte> value) => value.Length == 1 && value[0] <= 100
        ? new(BatteryReadStage.Value, BatteryReadOutcome.Reported, value[0])
        : new(BatteryReadStage.Value, BatteryReadOutcome.InvalidValue);

    internal static BluetoothBatteryReading Failure(BatteryReadStage stage, GattCommunicationStatus status) => new(stage, status switch
    {
        GattCommunicationStatus.AccessDenied => BatteryReadOutcome.AccessDenied,
        GattCommunicationStatus.Unreachable => BatteryReadOutcome.Unreachable,
        GattCommunicationStatus.ProtocolError => BatteryReadOutcome.ProtocolError,
        _ => BatteryReadOutcome.Unavailable,
    });
}
