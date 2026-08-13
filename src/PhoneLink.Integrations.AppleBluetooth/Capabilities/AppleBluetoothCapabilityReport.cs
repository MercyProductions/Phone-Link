namespace PhoneLink.Integrations.AppleBluetooth.Capabilities;

public sealed record AppleBluetoothCapabilityReport(
    AppleBluetoothCapability Capability,
    AppleBluetoothCapabilityStatus Status,
    DateTimeOffset ObservedAt,
    string ReasonCode)
{
    public bool IsReady => Status == AppleBluetoothCapabilityStatus.Ready;
}

