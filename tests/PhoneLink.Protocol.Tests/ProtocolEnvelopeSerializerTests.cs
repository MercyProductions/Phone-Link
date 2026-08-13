using System.Text;
using System.Text.Json;
using PhoneLink.Protocol.Messages;
using PhoneLink.Protocol.Serialization;
using PhoneLink.Protocol.Validation;

namespace PhoneLink.Protocol.Tests;

public sealed class ProtocolEnvelopeSerializerTests
{
    [Fact]
    public void RoundTripPreservesEnvelope()
    {
        var envelope = CreateEnvelope();

        var bytes = ProtocolEnvelopeSerializer.Serialize(envelope);
        var parsed = ProtocolEnvelopeSerializer.Parse(bytes);

        Assert.Equal(envelope.ProtocolVersion, parsed.ProtocolVersion);
        Assert.Equal(envelope.MessageId, parsed.MessageId);
        Assert.Equal(envelope.SessionId, parsed.SessionId);
        Assert.Equal(envelope.OriginDeviceId, parsed.OriginDeviceId);
        Assert.Equal(envelope.Kind, parsed.Kind);
        Assert.Equal(JsonValueKind.Object, parsed.Payload.ValueKind);
    }

    [Fact]
    public void ParseRejectsDuplicateProperties()
    {
        var envelope = CreateEnvelope();
        var validJson = Encoding.UTF8.GetString(ProtocolEnvelopeSerializer.Serialize(envelope));
        var duplicateJson = validJson.Replace(
            "\"protocolVersion\":1,",
            "\"protocolVersion\":1,\"protocolVersion\":1,",
            StringComparison.Ordinal);

        var exception = Assert.Throws<PhoneLinkProtocolException>(() =>
            ProtocolEnvelopeSerializer.Parse(Encoding.UTF8.GetBytes(duplicateJson)));

        Assert.Equal("protocol.json.duplicate-property", exception.Code);
    }

    [Fact]
    public void SerializeRejectsUnsupportedVersion()
    {
        var envelope = CreateEnvelope() with { ProtocolVersion = 99 };

        var exception = Assert.Throws<PhoneLinkProtocolException>(() =>
            ProtocolEnvelopeSerializer.Serialize(envelope));

        Assert.Equal("protocol.version.unsupported", exception.Code);
    }

    [Fact]
    public void ParseRejectsOversizedFrameBeforeJsonAllocation()
    {
        var oversized = new byte[ProtocolConstants.MaximumJsonFrameBytes + 1];

        var exception = Assert.Throws<PhoneLinkProtocolException>(() =>
            ProtocolEnvelopeSerializer.Parse(oversized));

        Assert.Equal("protocol.frame.too-large", exception.Code);
    }

    private static ProtocolEnvelope CreateEnvelope()
    {
        using var payloadDocument = JsonDocument.Parse("{\"capabilities\":[]}");

        return new ProtocolEnvelope
        {
            ProtocolVersion = ProtocolConstants.CurrentVersion,
            MessageId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            OriginDeviceId = Guid.NewGuid(),
            OriginSequence = 0,
            SentAt = DateTimeOffset.UtcNow,
            Kind = "session.ready",
            RequiresAck = false,
            Payload = payloadDocument.RootElement.Clone(),
        };
    }
}

