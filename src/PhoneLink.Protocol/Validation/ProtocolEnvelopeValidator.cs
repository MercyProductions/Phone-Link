using PhoneLink.Protocol.Messages;

namespace PhoneLink.Protocol.Validation;

internal static class ProtocolEnvelopeValidator
{
    public static void Validate(ProtocolEnvelope envelope)
    {
        if (envelope.ProtocolVersion != ProtocolConstants.CurrentVersion)
        {
            throw new PhoneLinkProtocolException(
                "protocol.version.unsupported",
                $"Protocol version {envelope.ProtocolVersion} is not supported.");
        }

        if (envelope.MessageId == Guid.Empty ||
            envelope.SessionId == Guid.Empty ||
            envelope.OriginDeviceId == Guid.Empty)
        {
            throw new PhoneLinkProtocolException(
                "protocol.identifier.empty",
                "Message, session, and origin device IDs must be non-empty UUIDs.");
        }

        if (envelope.OriginSequence < 0)
        {
            throw new PhoneLinkProtocolException(
                "protocol.sequence.invalid",
                "An origin sequence cannot be negative.");
        }

        if (string.IsNullOrWhiteSpace(envelope.Kind) ||
            envelope.Kind.Length > ProtocolConstants.MaximumKindLength ||
            !ProtocolConstants.KnownMessageKinds.Contains(envelope.Kind))
        {
            throw new PhoneLinkProtocolException(
                "protocol.kind.unsupported",
                "The message kind is missing or unsupported.");
        }

        if (envelope.Payload.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            throw new PhoneLinkProtocolException(
                "protocol.payload.not-object",
                "The envelope payload must be a JSON object.");
        }
    }
}

