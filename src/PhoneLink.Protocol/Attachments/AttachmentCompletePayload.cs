using System.Text.Json.Serialization;

namespace PhoneLink.Protocol.Attachments;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AttachmentCompletePayload
{
    public required Guid AttachmentId { get; init; }

    public required bool Verified { get; init; }

    public string? ErrorCode { get; init; }
}
