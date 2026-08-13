using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PhoneLink.Protocol.Validation;

namespace PhoneLink.Protocol.Pairing;

public static class PairingOfferCodec
{
    private const string Prefix = "phonelink://pair?data=";

    private static readonly HashSet<string> ExpectedRootProperties = new(StringComparer.Ordinal)
    {
        "version",
        "sessionId",
        "expiresAt",
        "endpoints",
        "tlsSpkiSha256",
        "serverIdentityKey",
        "offerNonce",
        "pairingSecret",
    };

    public static PairingOfferArtifact Encode(PairingOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);

        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", offer.Version);
            writer.WriteString("sessionId", offer.SessionId.ToString("D"));
            writer.WriteString("expiresAt", FormatTimestamp(offer.ExpiresAt));
            writer.WriteStartArray("endpoints");
            foreach (var endpoint in offer.Endpoints)
            {
                writer.WriteStartObject();
                writer.WriteString("host", endpoint.Host);
                writer.WriteNumber("port", endpoint.Port);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteString("tlsSpkiSha256", Base64Url.Encode(offer.TlsSpkiSha256.Span));
            writer.WriteString("serverIdentityKey", Base64Url.Encode(offer.ServerIdentityKey.Span));
            writer.WriteString("offerNonce", Base64Url.Encode(offer.OfferNonce.Span));
            writer.WriteString("pairingSecret", Base64Url.Encode(offer.PairingSecret.Span));
            writer.WriteEndObject();
            writer.Flush();
        }

        var rawJson = buffer.WrittenSpan.ToArray();
        var qrPayload = Prefix + Base64Url.Encode(rawJson);
        var artifact = new PairingOfferArtifact(offer, rawJson, qrPayload);
        Array.Clear(rawJson);
        return artifact;
    }

    public static PairingOfferArtifact Decode(string qrPayload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(qrPayload);

        if (!qrPayload.StartsWith(Prefix, StringComparison.Ordinal) ||
            qrPayload.AsSpan(Prefix.Length).IndexOfAny("?#&=") >= 0)
        {
            throw new PhoneLinkProtocolException(
                "pairing.offer.invalid-uri",
                "The QR code is not a canonical Phone Link pairing URI.");
        }

        byte[] rawJson;
        try
        {
            rawJson = Base64Url.Decode(qrPayload[Prefix.Length..]);
        }
        catch (FormatException exception)
        {
            throw new PhoneLinkProtocolException(
                "pairing.offer.invalid-data",
                "The QR code contains invalid encoded pairing data.",
                exception);
        }

        try
        {
            StrictJsonGuard.Validate(rawJson);
            using var document = JsonDocument.Parse(rawJson);
            var root = document.RootElement;
            RequireExactProperties(root, ExpectedRootProperties, "pairing offer");

            var version = GetRequiredInt32(root, "version");
            var sessionId = Guid.ParseExact(GetRequiredString(root, "sessionId"), "D");
            var expiresAt = DateTimeOffset.ParseExact(
                GetRequiredString(root, "expiresAt"),
                "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

            var endpointsElement = root.GetProperty("endpoints");
            if (endpointsElement.ValueKind != JsonValueKind.Array)
            {
                throw InvalidField("endpoints");
            }

            var endpoints = new List<PairingEndpoint>();
            foreach (var endpointElement in endpointsElement.EnumerateArray())
            {
                RequireExactProperties(
                    endpointElement,
                    new HashSet<string>(StringComparer.Ordinal) { "host", "port" },
                    "pairing endpoint");
                endpoints.Add(
                    new PairingEndpoint(
                        GetRequiredString(endpointElement, "host"),
                        GetRequiredInt32(endpointElement, "port")));
            }

            var offer = new PairingOffer(
                version,
                sessionId,
                expiresAt,
                endpoints,
                DecodeField(root, "tlsSpkiSha256"),
                DecodeField(root, "serverIdentityKey"),
                DecodeField(root, "offerNonce"),
                DecodeField(root, "pairingSecret"));

            return new PairingOfferArtifact(offer, rawJson, qrPayload);
        }
        catch (PhoneLinkProtocolException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException)
        {
            throw new PhoneLinkProtocolException(
                "pairing.offer.invalid",
                "The QR code contains an invalid pairing offer.",
                exception);
        }
        finally
        {
            Array.Clear(rawJson);
        }
    }

    private static byte[] DecodeField(JsonElement root, string propertyName)
    {
        try
        {
            return Base64Url.Decode(GetRequiredString(root, propertyName));
        }
        catch (FormatException exception)
        {
            throw new PhoneLinkProtocolException(
                "pairing.offer.invalid-field",
                $"Pairing field '{propertyName}' is not valid base64url.",
                exception);
        }
    }

    private static void RequireExactProperties(
        JsonElement element,
        IReadOnlySet<string> expected,
        string objectName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new PhoneLinkProtocolException(
                "pairing.offer.invalid-object",
                $"The {objectName} must be a JSON object.");
        }

        var actual = element.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected))
        {
            throw new PhoneLinkProtocolException(
                "pairing.offer.unexpected-properties",
                $"The {objectName} has missing or unsupported properties.");
        }
    }

    private static int GetRequiredInt32(JsonElement element, string propertyName)
    {
        var property = element.GetProperty(propertyName);
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value)
            ? value
            : throw InvalidField(propertyName);
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        var property = element.GetProperty(propertyName);
        return property.ValueKind == JsonValueKind.String && property.GetString() is { } value
            ? value
            : throw InvalidField(propertyName);
    }

    private static PhoneLinkProtocolException InvalidField(string propertyName) =>
        new(
            "pairing.offer.invalid-field",
            $"Pairing field '{propertyName}' has an invalid type or value.");

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
