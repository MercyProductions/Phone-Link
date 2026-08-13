using System.Text.Json;
using System.Text.Json.Serialization;
using PhoneLink.Protocol.Validation;

namespace PhoneLink.Protocol.Session;

public static class SessionWireSerializer
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };

    public static T Parse<T>(ReadOnlySpan<byte> utf8Json)
    {
        StrictJsonGuard.Validate(utf8Json);
        try
        {
            return JsonSerializer.Deserialize<T>(utf8Json, Options)
                ?? throw new PhoneLinkProtocolException(
                    "session.message.null",
                    "A session-authentication message cannot be null.");
        }
        catch (PhoneLinkProtocolException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new PhoneLinkProtocolException(
                "session.message.invalid",
                "The session-authentication message is invalid.",
                exception);
        }
    }

    public static byte[] Serialize<T>(T message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.SerializeToUtf8Bytes(message, Options);
    }
}
