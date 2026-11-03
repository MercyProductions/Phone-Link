using System.Text.Json.Serialization;

namespace PhoneLink.Protocol.Messages;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CompanionHealthSnapshotPayload
{
    public required int SchemaVersion { get; init; }
    public required DateTimeOffset CapturedAt { get; init; }
    public required int? BatteryPercent { get; init; }
    public required string BatteryState { get; init; }
    public required string ThermalState { get; init; }
    public required bool LowPowerMode { get; init; }
    public required string ContactsPermission { get; init; }
    public required string PhotosPermission { get; init; }
    public required string CameraPermission { get; init; }
    public required string NotificationPermission { get; init; }
}
