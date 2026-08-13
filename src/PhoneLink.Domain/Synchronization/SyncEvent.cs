using System.Text.Json;
using PhoneLink.Domain.Devices;

namespace PhoneLink.Domain.Synchronization;

public sealed record SyncEvent
{
    public const int MaximumEventTypeLength = 200;

    public SyncEvent(
        Guid eventId,
        DeviceId originDeviceId,
        long originSequence,
        DateTimeOffset occurredAt,
        string eventType,
        string entityId,
        JsonElement payload)
    {
        if (eventId == Guid.Empty)
        {
            throw new ArgumentException("An event ID cannot be empty.", nameof(eventId));
        }

        if (originSequence <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(originSequence),
                "An origin sequence must be positive.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
        if (eventType.Length > MaximumEventTypeLength)
        {
            throw new ArgumentOutOfRangeException(nameof(eventType));
        }

        EventId = eventId;
        OriginDeviceId = originDeviceId;
        OriginSequence = originSequence;
        OccurredAt = occurredAt;
        EventType = eventType;
        EntityId = entityId ?? string.Empty;
        Payload = payload.Clone();
    }

    public Guid EventId { get; }

    public DeviceId OriginDeviceId { get; }

    public long OriginSequence { get; }

    public DateTimeOffset OccurredAt { get; }

    public string EventType { get; }

    public string EntityId { get; }

    public JsonElement Payload { get; }
}

