using System.Text.Json;
using PhoneLink.Application.Features.Diagnostics;
using PhoneLink.DeviceCommunication.Diagnostics;
using PhoneLink.DeviceCommunication.Sessions;
using PhoneLink.Domain.Devices;
using PhoneLink.Protocol;
using PhoneLink.Protocol.Messages;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class CompanionHealthEnvelopeHandlerTests
{
    private static ReceivedDeviceEnvelope Request()
    {
        var device = DeviceId.New();
        return new(device, new ProtocolEnvelope
        {
            ProtocolVersion = 1, MessageId = Guid.NewGuid(), SessionId = Guid.NewGuid(),
            OriginDeviceId = device.Value, OriginSequence = 0, SentAt = DateTimeOffset.UtcNow,
            Kind = "diagnostics.health.snapshot", RequiresAck = true,
            Payload = JsonSerializer.SerializeToElement(new
            {
                schemaVersion = 1, capturedAt = DateTimeOffset.UtcNow, batteryPercent = (int?)null,
                batteryState = "unknown", thermalState = "fair", lowPowerMode = true,
                contactsPermission = "limited", photosPermission = "not-determined",
                cameraPermission = "restricted", notificationPermission = "denied",
            }),
        });
    }

    [Fact]
    public void ReceiptIsBoundToRequestServerAndSession()
    {
        var inbox = new CompanionHealthInbox(); var request = Request(); var server = Guid.NewGuid();
        var receipt = new CompanionHealthEnvelopeHandler(inbox).Handle(request, server, (_, _) => true, false, DateTimeOffset.UtcNow);
        Assert.NotNull(receipt);
        Assert.Equal(request.Envelope.MessageId, receipt.CorrelationId);
        Assert.Equal(request.SessionId, receipt.SessionId);
        Assert.Equal(server, receipt.OriginDeviceId);
        Assert.NotEqual(request.Envelope.MessageId, receipt.MessageId);
        Assert.False(receipt.RequiresAck);
        Assert.True(receipt.Payload.GetProperty("accepted").GetBoolean());
        Assert.DoesNotContain("battery", receipt.Payload.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(inbox.Get(request.DeviceId.Value, request.SessionId, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("session")]
    [InlineData("version")]
    [InlineData("kind")]
    public void UnauthorizedOrUnrelatedFramesGetNoReceipt(string changed)
    {
        var inbox = new CompanionHealthInbox(); var request = Request();
        request = request with { Envelope = changed switch
        {
            "origin" => request.Envelope with { OriginDeviceId = Guid.NewGuid() },
            "version" => request.Envelope with { ProtocolVersion = 2 },
            "kind" => request.Envelope with { Kind = "ping" },
            _ => request.Envelope,
        } };
        Assert.Null(new CompanionHealthEnvelopeHandler(inbox).Handle(request, Guid.NewGuid(), (_, _) => changed != "session", false, DateTimeOffset.UtcNow));
        Assert.Null(inbox.Get(request.DeviceId.Value, request.SessionId, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData("locked")]
    [InlineData("no-ack")]
    [InlineData("correlated")]
    [InlineData("malformed")]
    public void CurrentButInvalidSubmissionsReceiveFalseWithoutStorage(string invalid)
    {
        var inbox = new CompanionHealthInbox(); var request = Request();
        request = request with { Envelope = invalid switch
        {
            "no-ack" => request.Envelope with { RequiresAck = false },
            "correlated" => request.Envelope with { CorrelationId = Guid.NewGuid() },
            "malformed" => request.Envelope with { Payload = JsonSerializer.SerializeToElement(new { schemaVersion = 4 }) },
            _ => request.Envelope,
        } };
        var receipt = new CompanionHealthEnvelopeHandler(inbox).Handle(request, Guid.NewGuid(), (_, _) => true, invalid == "locked", DateTimeOffset.UtcNow);
        Assert.NotNull(receipt);
        Assert.False(receipt.Payload.GetProperty("accepted").GetBoolean());
        Assert.Null(inbox.Get(request.DeviceId.Value, request.SessionId, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SessionChangesDuringValidationRemoveOnlyTheOldSession()
    {
        var inbox = new CompanionHealthInbox(); var request = Request(); var newSession = Guid.NewGuid();
        var checks = 0;
        bool Current(DeviceId device, Guid session)
        {
            if (++checks == 1) return true;
            Assert.True(inbox.TryStoreJson(device.Value, newSession, request.Envelope.Payload, DateTimeOffset.UtcNow));
            return false;
        }
        var receipt = new CompanionHealthEnvelopeHandler(inbox).Handle(request, Guid.NewGuid(), Current, false, DateTimeOffset.UtcNow);
        Assert.Null(receipt);
        Assert.NotNull(inbox.Get(request.DeviceId.Value, newSession, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void InactiveSessionAfterValidationCannotKeepAQueuedSnapshot()
    {
        var inbox = new CompanionHealthInbox(); var request = Request(); var checks = 0;
        Assert.Null(new CompanionHealthEnvelopeHandler(inbox).Handle(request, Guid.NewGuid(), (_, _) => ++checks == 1, false, DateTimeOffset.UtcNow));
        Assert.Null(inbox.Get(request.DeviceId.Value, request.SessionId, DateTimeOffset.UtcNow));
    }
}
