using System.Text.Json.Serialization;

namespace PhoneLink.Protocol.Attachments;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AttachmentAcceptPayload
{
    public required Guid AttachmentId { get; init; }

    public required bool Accepted { get; init; }

    public required uint NextChunkIndex { get; init; }

    public string? ErrorCode { get; init; }
}
