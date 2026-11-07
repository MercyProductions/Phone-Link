using System.Text.Json;
using PhoneLink.Application.Features.BinaryInspection;
using PhoneLink.DeviceCommunication.Sessions;
using PhoneLink.Domain.Devices;
using PhoneLink.Protocol;
using PhoneLink.Protocol.Messages;

namespace PhoneLink.DeviceCommunication.Diagnostics;

/// <summary>One transient, explicitly armed view of a current authenticated companion session.</summary>
public sealed class CompanionRuntimeFeed
{
    private static readonly TimeSpan Freshness = TimeSpan.FromSeconds(6);
    private readonly object _gate = new();
    private bool _armed;
    private ReceivedSample? _latest;

    public void SetArmed(bool armed)
    {
        lock (_gate) { _armed = armed; _latest = null; }
    }

    public CompanionOwnImageSample? Get(DateTimeOffset now, Func<DeviceId, Guid, bool> isCurrentSession)
    {
        ArgumentNullException.ThrowIfNull(isCurrentSession);
        lock (_gate) return Prune(now, isCurrentSession)?.Sample;
    }

    public ProtocolEnvelope? Handle(ReceivedDeviceEnvelope received, Guid serverId, DateTimeOffset now,
        Func<DeviceId, Guid, bool> isCurrentSession)
    {
        ArgumentNullException.ThrowIfNull(received);
        ArgumentNullException.ThrowIfNull(isCurrentSession);
        var request = received.Envelope;
        if (request.Kind != "diagnostics.runtime.sample" || request.ProtocolVersion != ProtocolConstants.CurrentVersion ||
            request.MessageId == Guid.Empty || serverId == Guid.Empty || received.SessionId == Guid.Empty ||
            received.DeviceId.Value == Guid.Empty || request.OriginDeviceId != received.DeviceId.Value ||
            !request.RequiresAck || request.CorrelationId is not null || !isCurrentSession(received.DeviceId, received.SessionId)) return null;

        bool accepted = false;
        lock (_gate)
        {
            var previous = Prune(now, isCurrentSession);
            if (_armed && (previous is null || (previous.DeviceId == received.DeviceId && previous.SessionId == received.SessionId &&
                now - previous.ReceivedAt >= TimeSpan.FromMilliseconds(500))))
            {
                try
                {
                    var sample = CompanionRuntimeMapper.ParseOwnImageSample(request.Payload);
                    var age = now - sample.CapturedAt;
                    if (age >= TimeSpan.FromSeconds(-2) && age < Freshness &&
                        (previous is null || sample.CapturedAt > previous.Sample.CapturedAt) &&
                        isCurrentSession(received.DeviceId, received.SessionId))
                    {
                        _latest = new(received.DeviceId, received.SessionId, now, sample);
                        accepted = true;
                    }
                }
                catch (InvalidDataException) { /* Malformed addresses are rejected, never logged. */ }
            }
        }
        return new ProtocolEnvelope
        {
            ProtocolVersion = ProtocolConstants.CurrentVersion, MessageId = Guid.NewGuid(),
            CorrelationId = request.MessageId, SessionId = received.SessionId, OriginDeviceId = serverId,
            OriginSequence = 0, SentAt = now, Kind = "diagnostics.runtime.receipt", RequiresAck = false,
            Payload = JsonSerializer.SerializeToElement(new { accepted }),
        };
    }

    private ReceivedSample? Prune(DateTimeOffset now, Func<DeviceId, Guid, bool> isCurrentSession)
    {
        if (_latest is { } latest && (now < latest.ReceivedAt || now - latest.ReceivedAt >= Freshness ||
            now - latest.Sample.CapturedAt >= Freshness || !isCurrentSession(latest.DeviceId, latest.SessionId))) _latest = null;
        return _latest;
    }

    private sealed record ReceivedSample(DeviceId DeviceId, Guid SessionId, DateTimeOffset ReceivedAt, CompanionOwnImageSample Sample);
}
