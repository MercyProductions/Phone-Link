using System.Text.Json.Serialization;

namespace PhoneLink.Protocol.Session;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SessionClientProof
{
    public required string Kind { get; init; }

    public required Guid ConnectionId { get; init; }

    public required Guid SessionId { get; init; }

    public required string Signature { get; init; }
}
