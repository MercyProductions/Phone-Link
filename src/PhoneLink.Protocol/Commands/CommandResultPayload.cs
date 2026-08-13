using System.Text.Json.Serialization;

namespace PhoneLink.Protocol.Commands;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CommandResultPayload
{
    public required Guid CommandId { get; init; }

    public required string Status { get; init; }

    public string? ErrorCode { get; init; }

    public required DateTimeOffset FinishedAt { get; init; }
}
