using System.Text.Json.Serialization;
using PhoneLink.Protocol.Messages;

namespace PhoneLink.Protocol.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ProtocolEnvelope))]
internal sealed partial class ProtocolJsonContext : JsonSerializerContext
{
}
