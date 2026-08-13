using PhoneLink.Integrations.AppleBluetooth.Capabilities;

namespace PhoneLink.Windows.Pages;

public sealed class AppleBluetoothCapabilityListItem
{
    public string Name { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string Detail { get; set; } = string.Empty;

    public static AppleBluetoothCapabilityListItem FromReport(AppleBluetoothCapabilityReport report) =>
        new()
        {
            Name = GetName(report.Capability),
            Status = GetStatus(report.Status),
            Detail = GetDetail(report),
        };

    private static string GetName(AppleBluetoothCapability capability) => capability switch
    {
        AppleBluetoothCapability.AncsNotifications => "Receive iPhone notifications (ANCS)",
        AppleBluetoothCapability.AncsActions => "Notification actions (ANCS)",
        AppleBluetoothCapability.PbapContacts => "Bluetooth phone book (PBAP)",
        AppleBluetoothCapability.PbapCallHistory => "Bluetooth call history (PBAP)",
        AppleBluetoothCapability.MapObserveMessages => "Observe Bluetooth messages (MAP)",
        AppleBluetoothCapability.MapSendMessages => "Send Bluetooth messages (MAP)",
        AppleBluetoothCapability.MapReplyToMessages => "Reply to Bluetooth messages (MAP)",
        _ => capability.ToString(),
    };

    private static string GetStatus(AppleBluetoothCapabilityStatus status) => status switch
    {
        AppleBluetoothCapabilityStatus.Ready => "Available",
        AppleBluetoothCapabilityStatus.RequiresSystemPairing => "System pairing required",
        AppleBluetoothCapabilityStatus.RequiresUserPermission => "Permission required",
        AppleBluetoothCapabilityStatus.ReservedByOperatingSystem => "Not exposed to this app",
        _ => "Unavailable",
    };

    private static string GetDetail(AppleBluetoothCapabilityReport report) => report.Status switch
    {
        AppleBluetoothCapabilityStatus.Ready =>
            "Windows discovered the public Bluetooth service. Runtime access may still require an iPhone permission prompt.",
        AppleBluetoothCapabilityStatus.RequiresSystemPairing =>
            "Pair the iPhone in Windows Bluetooth settings, then refresh this check.",
        AppleBluetoothCapabilityStatus.RequiresUserPermission =>
            "Approve the corresponding permission on the iPhone, then refresh this check.",
        AppleBluetoothCapabilityStatus.ReservedByOperatingSystem =>
            "A Bluetooth device is paired, but Windows did not expose this service to Phone Link. The device may not support it, or the operating system may reserve the profile.",
        _ => $"The capability is unavailable ({report.ReasonCode}).",
    };
}
