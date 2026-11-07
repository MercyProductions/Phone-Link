using System.Buffers.Binary;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PhoneLink.Integrations.AppleBluetooth.Capabilities;

public sealed class MapInboxItem(string sender, string preview, string time, string type)
{
    public string Sender { get; } = sender;
    public string Preview { get; } = preview;
    public string Time { get; } = time;
    public string Type { get; } = type;
}

internal sealed class MapObexRejectedException(byte code) : IOException($"The phone returned OBEX status 0x{code:X2}.")
{
    public byte Code { get; } = code;
}

internal static class MapInboxClient
{
    private const int MaximumPacket = 4096;
    private const int MaximumListing = 64 * 1024;
    private const int MaximumItems = 20;
    private static readonly byte[] Target = Convert.FromHexString("BB582B40420C11DBB0DE0800200C9A66");

    internal static async Task<IReadOnlyList<MapInboxItem>> ReadAsync(Stream input, Stream output, bool sendSupportedFeatures, CancellationToken token)
    {
        var connected = await ExchangeAsync(input, output, 0x80,
            [0x10, 0, 0x10, 0x00, .. Variable(0x46, Target), .. Variable(0x4C, sendSupportedFeatures ? [0x29, 4, 0, 0, 0, 4] : [])], token).ConfigureAwait(false);
        RequireSuccess(connected.Code);
        if (connected.Payload.Length < 4 || (connected.Payload[0] & 0xf0) != 0x10 || connected.Payload[1] != 0 ||
            BinaryPrimitives.ReadUInt16BigEndian(connected.Payload.AsSpan(2, 2)) < 255)
            throw new InvalidDataException("The MAP connection response was malformed.");
        var headers = Headers(connected.Payload[4..]).ToArray();
        var who = headers.Where(item => item.Kind == 0x4A).ToArray();
        var ids = headers.Where(item => item.Kind == 0xCB).ToArray();
        if (who.Length != 1 || !who[0].Value.AsSpan().SequenceEqual(Target) || ids.Length != 1)
            throw new InvalidDataException("The phone did not identify the expected MAP service.");
        byte[] id = [0xCB, .. ids[0].Value];

        foreach (var folder in new[] { "telecom", "msg" })
        {
            var path = await ExchangeAsync(input, output, 0x85, [0x02, 0, .. id, .. Variable(0x01, Encoding.BigEndianUnicode.GetBytes(folder + '\0'))], token).ConfigureAwait(false);
            RequireSuccess(path.Code);
        }

        byte[] get = [.. id, .. Variable(0x01, Encoding.BigEndianUnicode.GetBytes("inbox\0")),
            .. Variable(0x42, "x-bt/MAP-msg-listing\0"u8),
            .. Variable(0x4C, [0x01, 2, 0, MaximumItems, 0x03, 1, 0x04, 0x10, 4, 0, 0, 0x10, 0x47, 0x13, 1, 128])];
        using var body = new MemoryStream();
        for (var round = 0; round < 32; round++)
        {
            var response = await ExchangeAsync(input, output, 0x83, round == 0 ? get : id, token).ConfigureAwait(false);
            if (response.Code != 0x90) RequireSuccess(response.Code);
            var end = false;
            foreach (var (kind, value) in Headers(response.Payload))
            {
                if (kind is not (0x48 or 0x49)) continue;
                if (end || body.Length + value.Length > MaximumListing) throw new InvalidDataException("The MAP listing exceeded its limits.");
                body.Write(value);
                end = kind == 0x49;
            }
            if (response.Code == 0x90 && !end) continue;
            if (response.Code != 0xA0 || !end) throw new InvalidDataException("The MAP listing ended without a complete body.");
            body.Position = 0;
            var items = Parse(body);
            // Disconnect is best-effort. No PUT, read-status mutation, or notification registration is issued.
            using var disconnect = CancellationTokenSource.CreateLinkedTokenSource(token);
            disconnect.CancelAfter(TimeSpan.FromSeconds(1));
            try { await ExchangeAsync(input, output, 0x81, id, disconnect.Token).ConfigureAwait(false); }
            catch (IOException) { }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            return items;
        }
        throw new InvalidDataException("Too many MAP continuation packets.");
    }

    internal static IReadOnlyList<MapInboxItem> Parse(Stream input)
    {
        using var reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, MaxCharactersInDocument = MaximumListing,
        });
        var root = XDocument.Load(reader).Root;
        if (root?.Name != "MAP-msg-listing" || root.Attribute("version")?.Value is not ("1.0" or "1.1"))
            throw new InvalidDataException("The response is not a supported MAP message listing.");
        var messages = root.Elements().Take(MaximumItems + 1).ToArray();
        if (messages.Length > MaximumItems || messages.Any(item => item.Name != "msg" || item.HasElements))
            throw new InvalidDataException("The MAP listing exceeded its entry limits or contained invalid entries.");
        return messages.Where(item => item.Attribute("type")?.Value is "SMS_GSM" or "SMS_CDMA" or "MMS" or "IM")
            .Select(item => new MapInboxItem(Text(item, "sender_name", 128), Text(item, "subject", 128),
            Text(item, "datetime", 24), Text(item, "type", 16))).ToArray();
    }

    private static string Text(XElement item, string name, int maximum) =>
        new((item.Attribute(name)?.Value ?? string.Empty).Where(character => !char.IsControl(character)).Take(maximum).ToArray());

    private static byte[] Variable(byte kind, ReadOnlySpan<byte> data)
    {
        var header = new byte[data.Length + 3];
        header[0] = kind;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(1), checked((ushort)header.Length));
        data.CopyTo(header.AsSpan(3));
        return header;
    }

    private static IEnumerable<(byte Kind, byte[] Value)> Headers(byte[] data)
    {
        for (var offset = 0; offset < data.Length;)
        {
            var kind = data[offset];
            var prefix = 1;
            var length = (kind & 0xc0) switch { 0x80 => 2, 0xc0 => 5, _ => 0 };
            if (length == 0)
            {
                prefix = 3;
                if (data.Length - offset < prefix) throw new InvalidDataException("An OBEX header was truncated.");
                length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 1, 2));
            }
            if (length < prefix || length > data.Length - offset) throw new InvalidDataException("An OBEX header had an invalid length.");
            yield return (kind, data[(offset + prefix)..(offset + length)]);
            offset += length;
        }
    }

    private static async Task<(byte Code, byte[] Payload)> ExchangeAsync(Stream input, Stream output, byte code, byte[] body, CancellationToken token)
    {
        var packet = new byte[body.Length + 3];
        packet[0] = code;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(1), checked((ushort)packet.Length));
        body.CopyTo(packet, 3);
        await output.WriteAsync(packet, token).ConfigureAwait(false);
        await output.FlushAsync(token).ConfigureAwait(false);
        var header = new byte[3];
        await input.ReadExactlyAsync(header, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(1));
        if (length is < 3 or > MaximumPacket) throw new InvalidDataException("An OBEX packet exceeded its bounds.");
        var payload = new byte[length - 3];
        await input.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        return (header[0], payload);
    }

    private static void RequireSuccess(byte code)
    {
        if (code != 0xA0) throw new MapObexRejectedException(code);
    }
}
