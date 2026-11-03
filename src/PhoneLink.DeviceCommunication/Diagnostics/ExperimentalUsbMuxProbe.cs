using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace PhoneLink.DeviceCommunication.Diagnostics;

public sealed record UsbMuxInventory(int UsbConnections, int NetworkConnections, int OtherConnections);

// Experimental, independently implemented read-only probe of the local Apple transport.
// No pairing records, Connect/StartService, device commands, or firmware operations.
public static class ExperimentalUsbMuxProbe
{
    private const int MaximumReplyBytes = 1_048_576;

    public static async Task<UsbMuxInventory> ProbeAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        using var client = new TcpClient(AddressFamily.InterNetwork);
        // Fixed loopback endpoint only; no LAN or phone port scanning.
        await client.ConnectAsync(IPAddress.Loopback, 27015, timeout.Token).ConfigureAwait(false);
        return await ExchangeAsync(client.GetStream(), timeout.Token).ConfigureAwait(false);
    }

    public static async Task<UsbMuxInventory> ExchangeAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        var body = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict>
            <key>MessageType</key><string>ListDevices</string>
            <key>ClientVersionString</key><string>PhoneLink-Experimental-1</string>
            <key>ProgName</key><string>PhoneLink</string>
            <key>kLibUSBMuxVersion</key><integer>3</integer>
            </dict></plist>
            """);
        var payload = await ExchangePayloadAsync(stream, body, cancellationToken).ConfigureAwait(false);
        try { return ParseInventory(payload); }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    internal static async Task<byte[]> ExchangePayloadAsync(Stream stream, byte[] body, CancellationToken cancellationToken)
    {
        var request = new byte[16 + body.Length];
        BinaryPrimitives.WriteInt32LittleEndian(request, request.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(12), 1);
        body.CopyTo(request, 16);
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        var header = new byte[16];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length <= 16 || length > MaximumReplyBytes ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != 1 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)) != 8 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)) != 1)
            throw new InvalidDataException("The local transport returned an unsupported frame.");
        var payload = new byte[checked((int)length - 16)];
        try
        {
            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            return payload;
        }
        catch { CryptographicOperations.ZeroMemory(payload); throw; }
    }

    public static UsbMuxInventory ParseInventory(byte[] payload)
    {
        var dictionary = ParseDictionary(payload);
        if (!dictionary.TryGetValue("DeviceList", out var list) || list.Name != "array")
            throw new InvalidDataException("The local transport did not return a device inventory.");
        var usb = 0;
        var network = 0;
        var other = 0;
        foreach (var device in list.Elements())
        {
            if (usb + network + other >= 256) throw new InvalidDataException("The local transport inventory exceeds the limit.");
            var entry = ReadDictionary(device);
            if (!entry.TryGetValue("Properties", out var properties))
                throw new InvalidDataException("The local transport inventory entry is incomplete.");
            var values = ReadDictionary(properties);
            var type = values.TryGetValue("ConnectionType", out var connection) && connection.Name == "string"
                ? connection.Value : string.Empty;
            switch (type)
            {
                case "USB": usb++; break;
                case "Network": network++; break;
                default: other++; break;
            }
        }
        return new(usb, network, other);
    }

    internal static Dictionary<string, XElement> ParseDictionary(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length > MaximumReplyBytes) throw new InvalidDataException("The local transport reply exceeds the limit.");
        using var source = new MemoryStream(payload, writable: false);
        using var reader = XmlReader.Create(source, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore, XmlResolver = null,
            MaxCharactersInDocument = MaximumReplyBytes,
        });
        var document = XDocument.Load(reader);
        if (document.Root is not { Name.LocalName: "plist" } root || root.Elements().Count() != 1)
            throw new InvalidDataException("The local transport did not return a property list.");
        return ReadDictionary(root.Elements().Single());
    }

    internal static Dictionary<string, XElement> ReadDictionary(XElement element)
    {
        if (element.Name != "dict") throw new InvalidDataException("A transport dictionary is invalid.");
        var children = element.Elements().ToArray();
        if (children.Length % 2 != 0) throw new InvalidDataException("A transport dictionary is incomplete.");
        Dictionary<string, XElement> result = new(StringComparer.Ordinal);
        for (var index = 0; index < children.Length; index += 2)
        {
            if (children[index].Name != "key" || children[index].HasElements || !result.TryAdd(children[index].Value, children[index + 1]))
                throw new InvalidDataException("A transport dictionary contains an invalid or duplicate key.");
        }
        return result;
    }
}
