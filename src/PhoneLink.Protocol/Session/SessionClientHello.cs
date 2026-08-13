using System.Text.Json.Serialization;

namespace PhoneLink.Protocol.Session;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SessionClientHello
{
    public required string Kind { get; init; }

    public required Guid ConnectionId { get; init; }

    public required Guid ClientDeviceId { get; init; }

    public required string ClientNonce { get; init; }

    public required int ProtocolFloor { get; init; }

    public required int ProtocolCeiling { get; init; }

    public required string AppVersion { get; init; }
}
