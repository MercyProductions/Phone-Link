using System.Globalization;
using System.Text.Json;
using PhoneLink.Protocol.Messages;

namespace PhoneLink.Application.Features.Diagnostics;

public sealed record ReceivedCompanionHealth(CompanionHealthSnapshotPayload Snapshot, DateTimeOffset ReceivedAt)
{
    public string ToDisplayText() => $"Phone-reported capture: {Snapshot.CapturedAt:O}; Windows receipt: {ReceivedAt:O}.\n" +
        $"Charge: {(Snapshot.BatteryPercent is { } percent ? percent.ToString(CultureInfo.InvariantCulture) + "%" : "unknown")} · state: {Snapshot.BatteryState} · low-power mode: {Snapshot.LowPowerMode}.\n" +
        $"Thermal pressure: {Snapshot.ThermalState} (not a temperature measurement).\n" +
        $"Companion permissions — contacts: {Snapshot.ContactsPermission}; photos: {Snapshot.PhotosPermission}; camera: {Snapshot.CameraPermission}; notifications: {Snapshot.NotificationPermission}.\n" +
        (Snapshot.ThermalState is "serious" or "critical" ? "iOS reports elevated thermal pressure. Reduce demanding activity and let the phone cool naturally; this does not establish a hardware fault.\n" : "") +
        "This user-shared snapshot is not live monitoring, battery maximum capacity, charging wattage or hardware certification. Storage stays on iPhone. No permission, setting, data or firmware was changed.";
}

/// <summary>Bounded, memory-only, session-bound receipts. Input consent is provided on the sending phone.</summary>
public sealed class CompanionHealthInbox
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Entry> _entries = [];
    private bool _locked;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        { NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict };
    public static TimeSpan Lifetime { get; } = TimeSpan.FromMinutes(5);

    public bool TryStoreJson(Guid deviceId, Guid sessionId, JsonElement json, DateTimeOffset receivedAt)
    {
        try
        {
            if (json.GetRawText().Length > 4096) return false;
            var snapshot = json.Deserialize<CompanionHealthSnapshotPayload>(JsonOptions);
            return snapshot is not null && TryStore(deviceId, sessionId, snapshot, receivedAt);
        }
        catch (JsonException) { return false; }
    }

    public bool TryStore(Guid deviceId, Guid sessionId, CompanionHealthSnapshotPayload snapshot, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (deviceId == Guid.Empty || sessionId == Guid.Empty || !IsValid(snapshot)) return false;
        lock (_gate)
        {
            if (_locked) return false;
            foreach (var key in _entries.Where(pair => receivedAt - pair.Value.Report.ReceivedAt >= Lifetime).Select(pair => pair.Key).ToArray()) _entries.Remove(key);
            if (_entries.TryGetValue(deviceId, out var previous) && previous.Report.ReceivedAt > receivedAt) return false;
            if (!_entries.ContainsKey(deviceId) && _entries.Count >= 16) return false;
            _entries[deviceId] = new(sessionId, new(snapshot, receivedAt));
            return true;
        }
    }

    public ReceivedCompanionHealth? Get(Guid deviceId, Guid activeSessionId, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_locked || !_entries.TryGetValue(deviceId, out var entry)) return null;
            var age = now - entry.Report.ReceivedAt;
            if (entry.SessionId != activeSessionId || age < TimeSpan.Zero || age >= Lifetime)
            { _entries.Remove(deviceId); return null; }
            return entry.Report;
        }
    }

    public void Remove(Guid deviceId) { lock (_gate) _entries.Remove(deviceId); }
    public void Remove(Guid deviceId, Guid sessionId)
    {
        lock (_gate)
            if (_entries.TryGetValue(deviceId, out var entry) && entry.SessionId == sessionId) _entries.Remove(deviceId);
    }
    public void SetPrivacyLocked(bool locked) { lock (_gate) { _locked = locked; _entries.Clear(); } }

    public static bool IsValid(CompanionHealthSnapshotPayload value) => value.SchemaVersion == 1 &&
        value.CapturedAt != default && value.BatteryPercent is null or >= 0 and <= 100 &&
        value.BatteryState is "unknown" or "unplugged" or "charging" or "full" &&
        value.ThermalState is "unknown" or "nominal" or "fair" or "serious" or "critical" &&
        Permission(value.ContactsPermission) && Permission(value.PhotosPermission) &&
        value.CameraPermission is "unknown" or "not-determined" or "restricted" or "denied" or "authorized" &&
        value.NotificationPermission is "unknown" or "not-determined" or "denied" or "authorized" or "provisional" or "ephemeral";

    private static bool Permission(string value) => value is "unknown" or "not-determined" or "restricted" or "denied" or "authorized" or "limited";
    private sealed record Entry(Guid SessionId, ReceivedCompanionHealth Report);
}
