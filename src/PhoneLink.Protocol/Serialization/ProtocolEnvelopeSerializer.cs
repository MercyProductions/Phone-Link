using System.Text.Json;
using PhoneLink.Protocol.Messages;
using PhoneLink.Protocol.Validation;

namespace PhoneLink.Protocol.Serialization;

public static class ProtocolEnvelopeSerializer
{
    public static ProtocolEnvelope Parse(ReadOnlySpan<byte> utf8Json)
    {
        StrictJsonGuard.Validate(utf8Json);

        try
        {
            var envelope = JsonSerializer.Deserialize(
                utf8Json,
                ProtocolJsonContext.Default.ProtocolEnvelope)
                ?? throw new PhoneLinkProtocolException(
                    "protocol.envelope.null",
                    "A protocol envelope cannot be null.");

            ProtocolEnvelopeValidator.Validate(envelope);
            return envelope;
        }
        catch (PhoneLinkProtocolException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new PhoneLinkProtocolException(
                "protocol.envelope.invalid",
                "The JSON object is not a valid protocol envelope.",
                exception);
        }
    }

    public static byte[] Serialize(ProtocolEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ProtocolEnvelopeValidator.Validate(envelope);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            ProtocolJsonContext.Default.ProtocolEnvelope);

        if (bytes.Length > ProtocolConstants.MaximumJsonFrameBytes)
        {
            throw new PhoneLinkProtocolException(
                "protocol.frame.too-large",
                $"A JSON frame cannot exceed {ProtocolConstants.MaximumJsonFrameBytes} bytes.");
        }

        return bytes;
    }
}

