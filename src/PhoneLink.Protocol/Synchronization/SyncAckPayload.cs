using System.Text.Json.Serialization;

namespace PhoneLink.Protocol.Synchronization;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SyncAckPayload
{
    public required Guid BatchId { get; init; }

    public required Guid OriginDeviceId { get; init; }

    public required long HighestContiguousSequence { get; init; }
}
