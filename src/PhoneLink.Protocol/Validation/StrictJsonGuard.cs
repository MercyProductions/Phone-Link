using System.Text.Json;

namespace PhoneLink.Protocol.Validation;

internal static class StrictJsonGuard
{
    public static void Validate(ReadOnlySpan<byte> utf8Json)
    {
        if (utf8Json.Length == 0)
        {
            throw new PhoneLinkProtocolException(
                "protocol.frame.empty",
                "A protocol frame cannot be empty.");
        }

        if (utf8Json.Length > ProtocolConstants.MaximumJsonFrameBytes)
        {
            throw new PhoneLinkProtocolException(
                "protocol.frame.too-large",
                $"A JSON frame cannot exceed {ProtocolConstants.MaximumJsonFrameBytes} bytes.");
        }

        var reader = new Utf8JsonReader(
            utf8Json,
            new JsonReaderOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = ProtocolConstants.MaximumJsonDepth,
            });

        var scopes = new Stack<HashSet<string>?>();
        var firstToken = true;

        try
        {
            while (reader.Read())
            {
                if (firstToken)
                {
                    firstToken = false;
                    if (reader.TokenType != JsonTokenType.StartObject)
                    {
                        throw new PhoneLinkProtocolException(
                            "protocol.json.root-not-object",
                            "A protocol frame must contain one JSON object.");
                    }
                }

                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                        break;
                    case JsonTokenType.StartArray:
                        scopes.Push(null);
                        break;
                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        if (scopes.Count == 0)
                        {
                            throw new PhoneLinkProtocolException(
                                "protocol.json.invalid",
                                "The JSON frame has an invalid container boundary.");
                        }

                        scopes.Pop();
                        break;
                    case JsonTokenType.PropertyName:
                        if (scopes.Count == 0 || scopes.Peek() is not { } properties)
                        {
                            throw new PhoneLinkProtocolException(
                                "protocol.json.invalid-property",
                                "A JSON property appeared outside an object.");
                        }

                        var propertyName = reader.GetString()
                            ?? throw new PhoneLinkProtocolException(
                                "protocol.json.invalid-property",
                                "A JSON property name cannot be null.");

                        if (!properties.Add(propertyName))
                        {
                            throw new PhoneLinkProtocolException(
                                "protocol.json.duplicate-property",
                                $"The JSON property '{propertyName}' occurs more than once in the same object.");
                        }

                        break;
                }
            }
        }
        catch (JsonException exception)
        {
            throw new PhoneLinkProtocolException(
                "protocol.json.invalid",
                "The protocol frame is not valid strict JSON.",
                exception);
        }

        if (firstToken || scopes.Count != 0)
        {
            throw new PhoneLinkProtocolException(
                "protocol.json.incomplete",
                "The protocol frame contains incomplete JSON.");
        }
    }
}

