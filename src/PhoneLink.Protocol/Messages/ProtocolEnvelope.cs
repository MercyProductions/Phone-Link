using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhoneLink.Protocol.Messages;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProtocolEnvelope
{
    public required int ProtocolVersion { get; init; }

    public required Guid MessageId { get; init; }

    public Guid? CorrelationId { get; init; }

    public required Guid SessionId { get; init; }

    public required Guid OriginDeviceId { get; init; }

    public required long OriginSequence { get; init; }

    public required DateTimeOffset SentAt { get; init; }

    public required string Kind { get; init; }

    public required bool RequiresAck { get; init; }

    public required JsonElement Payload { get; init; }
}

