using System.Text.Json.Serialization;

namespace PhoneLink.Protocol.Session;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SessionReady
{
    public required string Kind { get; init; }

    public required Guid SessionId { get; init; }

    public required Guid ServerDeviceId { get; init; }

    public required int ProtocolVersion { get; init; }

    public required string[] Capabilities { get; init; }

    public required int MaximumBatchEvents { get; init; }

    public required long MaximumAttachmentBytes { get; init; }
}
