using System.Text.Json.Serialization;

namespace PhoneLink.Protocol.Commands;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CommandRequestPayload
{
    public required Guid CommandId { get; init; }

    public required string Name { get; init; }

    public required IReadOnlyDictionary<string, string> Arguments { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required bool RequiresUserConfirmation { get; init; }
}
