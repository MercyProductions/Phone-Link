using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhoneLink.Protocol.Synchronization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SyncEventMessage
{
    public required Guid EventId { get; init; }

    public required long OriginSequence { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public required string EventType { get; init; }

    public required string EntityId { get; init; }

    public required JsonElement Payload { get; init; }
}
