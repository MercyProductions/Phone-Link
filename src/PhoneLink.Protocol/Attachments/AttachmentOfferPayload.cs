using System.Text.Json.Serialization;

namespace PhoneLink.Protocol.Attachments;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AttachmentOfferPayload
{
    public required Guid AttachmentId { get; init; }

    public required string DisplayName { get; init; }

    public required string MediaType { get; init; }

    public required long ByteCount { get; init; }

    public required string ContentSha256 { get; init; }

    public required int ChunkSize { get; init; }
}
