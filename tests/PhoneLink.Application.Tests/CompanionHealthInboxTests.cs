using System.Text.Json;
using PhoneLink.Application.Features.Diagnostics;
using PhoneLink.Protocol.Messages;

namespace PhoneLink.Application.Tests;

public sealed class CompanionHealthInboxTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-03T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static CompanionHealthSnapshotPayload Snapshot => new()
    {
        SchemaVersion = 1, CapturedAt = Now, BatteryPercent = null, BatteryState = "unknown",
        ThermalState = "nominal", LowPowerMode = false, ContactsPermission = "limited",
        PhotosPermission = "limited", CameraPermission = "not-determined", NotificationPermission = "provisional",
    };

    [Fact]
    public void ReportIsSessionBoundAndLatestOnly()
    {
        var inbox = new CompanionHealthInbox(); var device = Guid.NewGuid(); var session = Guid.NewGuid();
        Assert.True(inbox.TryStore(device, session, Snapshot, Now));
        Assert.True(inbox.TryStore(device, session, Snapshot with { BatteryPercent = 72 }, Now.AddSeconds(1)));
        Assert.False(inbox.TryStore(device, session, Snapshot, Now));
        Assert.Equal(72, inbox.Get(device, session, Now.AddSeconds(2))!.Snapshot.BatteryPercent);
        Assert.Null(inbox.Get(device, Guid.NewGuid(), Now.AddSeconds(2)));
        Assert.Null(inbox.Get(device, session, Now.AddSeconds(2)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void InvalidChargeRejected(int level) => Assert.False(CompanionHealthInbox.IsValid(Snapshot with { BatteryPercent = level }));

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void BoundaryChargeAccepted(int level) => Assert.True(CompanionHealthInbox.IsValid(Snapshot with { BatteryPercent = level }));

    [Fact]
    public void UnknownTokensAndSchemaRejected()
    {
        Assert.False(CompanionHealthInbox.IsValid(Snapshot with { SchemaVersion = 2 }));
        Assert.False(CompanionHealthInbox.IsValid(Snapshot with { ThermalState = "SECRET" }));
        Assert.False(CompanionHealthInbox.IsValid(Snapshot with { BatteryState = "SECRET" }));
        Assert.False(CompanionHealthInbox.IsValid(Snapshot with { ContactsPermission = "SECRET" }));
        Assert.False(CompanionHealthInbox.IsValid(Snapshot with { NotificationPermission = "SECRET" }));
        Assert.False(CompanionHealthInbox.IsValid(Snapshot with { CapturedAt = default }));
    }

    [Theory]
    [InlineData(300)]
    [InlineData(301)]
    [InlineData(-1)]
    public void ExpiredOrClockReversedReceiptIsNotDisplayed(int seconds)
    {
        var inbox = new CompanionHealthInbox(); var device = Guid.NewGuid(); var session = Guid.NewGuid();
        Assert.True(inbox.TryStore(device, session, Snapshot, Now));
        Assert.Null(inbox.Get(device, session, Now.AddSeconds(seconds)));
    }

    [Fact]
    public void PrivacyLockClearsAndBlocksUntilUnlocked()
    {
        var inbox = new CompanionHealthInbox(); var device = Guid.NewGuid(); var session = Guid.NewGuid();
        Assert.True(inbox.TryStore(device, session, Snapshot, Now));
        inbox.SetPrivacyLocked(true);
        Assert.Null(inbox.Get(device, session, Now));
        Assert.False(inbox.TryStore(device, session, Snapshot, Now));
        inbox.SetPrivacyLocked(false);
        Assert.Null(inbox.Get(device, session, Now));
        Assert.True(inbox.TryStore(device, session, Snapshot, Now));
        inbox.Remove(device);
        Assert.Null(inbox.Get(device, session, Now));
    }

    [Fact]
    public void BoundAndExpiryPruningDoNotGrowWithoutLimit()
    {
        var inbox = new CompanionHealthInbox(); var session = Guid.NewGuid();
        for (var index = 0; index < 16; index++) Assert.True(inbox.TryStore(Guid.NewGuid(), session, Snapshot, Now));
        Assert.False(inbox.TryStore(Guid.NewGuid(), session, Snapshot, Now));
        Assert.True(inbox.TryStore(Guid.NewGuid(), session, Snapshot, Now.AddMinutes(5)));
    }

    [Fact]
    public void StrictWirePayloadRejectsExtraMissingOrWrongFields()
    {
        var inbox = new CompanionHealthInbox(); var device = Guid.NewGuid(); var session = Guid.NewGuid();
        var json = JsonSerializer.Serialize(Snapshot, JsonOptions);
        Assert.True(inbox.TryStoreJson(device, session, JsonDocument.Parse(json).RootElement, Now));
        Assert.False(inbox.TryStoreJson(device, session, JsonDocument.Parse("{}").RootElement, Now));
        Assert.False(inbox.TryStoreJson(device, session, JsonDocument.Parse(json.Replace("\"schemaVersion\":1", "\"schemaVersion\":\"1\"", StringComparison.Ordinal)).RootElement, Now));
        Assert.False(inbox.TryStoreJson(device, session, JsonDocument.Parse(json[..^1] + ",\"storageBytes\":123}").RootElement, Now));
        Assert.False(inbox.TryStoreJson(device, session, JsonDocument.Parse("\"" + new string('x', 4097) + "\"").RootElement, Now));
    }

    [Fact]
    public void UnknownChargeAndThermalGuidanceDoNotClaimHardwareFault()
    {
        var report = new ReceivedCompanionHealth(Snapshot with { ThermalState = "serious" }, Now).ToDisplayText();
        Assert.Contains("Charge: unknown", report);
        Assert.Contains("not a temperature", report);
        Assert.Contains("does not establish a hardware fault", report);
        Assert.Contains("Storage stays on iPhone", report);
    }

    [Fact]
    public void EmptyTransportIdentityIsRejected()
    {
        var inbox = new CompanionHealthInbox();
        Assert.False(inbox.TryStore(Guid.Empty, Guid.NewGuid(), Snapshot, Now));
        Assert.False(inbox.TryStore(Guid.NewGuid(), Guid.Empty, Snapshot, Now));
    }
}
