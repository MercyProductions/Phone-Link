using System.Text.Json.Serialization;

namespace PhoneLink.Protocol.Session;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SessionServerChallenge
{
    public required string Kind { get; init; }

    public required Guid ConnectionId { get; init; }

    public required Guid SessionId { get; init; }

    public required Guid ServerDeviceId { get; init; }

    public required string ServerNonce { get; init; }

    public required int SelectedProtocolVersion { get; init; }

    public required DateTimeOffset ServerTime { get; init; }

    public required string Signature { get; init; }
}
