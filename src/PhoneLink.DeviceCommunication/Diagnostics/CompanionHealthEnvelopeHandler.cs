using System.Text.Json;
using PhoneLink.Application.Features.Diagnostics;
using PhoneLink.DeviceCommunication.Sessions;
using PhoneLink.Domain.Devices;
using PhoneLink.Protocol;
using PhoneLink.Protocol.Messages;

namespace PhoneLink.DeviceCommunication.Diagnostics;

/// <summary>Receives only authenticated, currently active, phone-originated snapshots. Does not request readings.</summary>
public sealed class CompanionHealthEnvelopeHandler(CompanionHealthInbox inbox)
{
    private readonly CompanionHealthInbox _inbox = inbox ?? throw new ArgumentNullException(nameof(inbox));

    public ProtocolEnvelope? Handle(ReceivedDeviceEnvelope received, Guid serverDeviceId,
        Func<DeviceId, Guid, bool> isCurrentSession, bool privacyLocked, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(received);
        ArgumentNullException.ThrowIfNull(isCurrentSession);
        var request = received.Envelope;
        if (request.Kind != "diagnostics.health.snapshot" || request.ProtocolVersion != ProtocolConstants.CurrentVersion ||
            request.MessageId == Guid.Empty || received.DeviceId.Value == Guid.Empty ||
            request.SessionId == Guid.Empty || request.OriginDeviceId != received.DeviceId.Value ||
            serverDeviceId == Guid.Empty || !isCurrentSession(received.DeviceId, received.SessionId)) return null;

        var accepted = !privacyLocked && request.RequiresAck && request.CorrelationId is null &&
            _inbox.TryStoreJson(received.DeviceId.Value, received.SessionId, request.Payload, now);

        // A queued frame must not survive a disconnect/replacement during validation.
        if (!isCurrentSession(received.DeviceId, received.SessionId))
        {
            _inbox.Remove(received.DeviceId.Value, received.SessionId);
            return null;
        }
        return new ProtocolEnvelope
        {
            ProtocolVersion = ProtocolConstants.CurrentVersion, MessageId = Guid.NewGuid(),
            CorrelationId = request.MessageId, SessionId = received.SessionId,
            OriginDeviceId = serverDeviceId, OriginSequence = 0, SentAt = now,
            Kind = "diagnostics.health.receipt", RequiresAck = false,
            Payload = JsonSerializer.SerializeToElement(new { accepted }),
        };
    }
}
