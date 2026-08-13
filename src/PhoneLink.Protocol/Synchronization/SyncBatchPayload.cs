using System.Text.Json.Serialization;

namespace PhoneLink.Protocol.Synchronization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SyncBatchPayload
{
    public required Guid BatchId { get; init; }

    public required SyncEventMessage[] Events { get; init; }
}
