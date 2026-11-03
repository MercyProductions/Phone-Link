using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace PhoneLink.Integrations.AppleBluetooth.Ancs;

public sealed record AncsCharacteristicSupport(bool Notifications, bool Actions, bool AccessDenied)
{
    public static AncsCharacteristicSupport Evaluate(GattCommunicationStatus status,
        IReadOnlyDictionary<Guid, GattCharacteristicProperties> characteristics)
    {
        ArgumentNullException.ThrowIfNull(characteristics);
        if (status != GattCommunicationStatus.Success) return new(false, false, status == GattCommunicationStatus.AccessDenied);
        bool Has(string uuid, GattCharacteristicProperties property) =>
            characteristics.TryGetValue(Guid.Parse(uuid), out var properties) && properties.HasFlag(property);
        var notifications = Has("9fbf120d-6301-42d9-8c58-25e699a21dbd", GattCharacteristicProperties.Notify);
        var actions = notifications && Has("69d1d8f3-45e1-49a8-9821-9bbdfdaad9d9", GattCharacteristicProperties.Write) &&
            Has("22eac6e9-24d6-4bb5-be44-b36ace7c7bfb", GattCharacteristicProperties.Notify);
        return new(notifications, actions, false);
    }
}
