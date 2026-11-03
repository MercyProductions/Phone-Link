using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using PhoneLink.DeviceCommunication.Diagnostics;

namespace PhoneLink.DeviceCommunication.Tunnels;

public sealed class PinnedUsbDeviceConnector
{
    private readonly byte[] _identityHash;
    private PinnedUsbDeviceConnector(byte[] identityHash) { _identityHash = identityHash; }
    public override string ToString() => "Pinned owned USB device (identifier redacted)";

    public static async Task<PinnedUsbDeviceConnector> SelectSingleAsync(CancellationToken cancellationToken)
    {
        var targets = await GetTargetsAsync(cancellationToken).ConfigureAwait(false);
        if (targets.Count != 1) throw new InvalidOperationException("Connect exactly one owned USB device before starting a forwarder.");
        return new(targets[0].IdentityHash);
    }

    public async Task<TunnelConnection> OpenChannelAsync(ushort remotePort, CancellationToken cancellationToken)
    {
        if (remotePort == 0) throw new ArgumentOutOfRangeException(nameof(remotePort));
        var targets = await GetTargetsAsync(cancellationToken).ConfigureAwait(false);
        var matches = targets.Where(target => CryptographicOperations.FixedTimeEquals(target.IdentityHash, _identityHash)).ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("The pinned USB device is absent or ambiguous; no replacement device was selected.");
        var client = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, 27015, cancellationToken).ConfigureAwait(false);
            var response = await ExperimentalIphoneInformationProbe.QueryMuxAsync(client.GetStream(),
                [new XElement("key", "MessageType"), new XElement("string", "Connect"),
                 new XElement("key", "DeviceID"), new XElement("integer", matches[0].Id),
                 new XElement("key", "PortNumber"), new XElement("integer", BinaryPrimitives.ReverseEndianness(remotePort))],
                cancellationToken).ConfigureAwait(false);
            if (!response.TryGetValue("MessageType", out var type) || type.Name != "string" || type.Value != "Result" ||
                !response.TryGetValue("Number", out var number) || number.Name != "integer" ||
                !int.TryParse(number.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
                throw new InvalidDataException("The device transport returned an unsupported channel response.");
            if (result != 0) throw new UsbMuxChannelUnavailableException(result);
            return new TunnelConnection(client.GetStream(), client, () => client.Client.Shutdown(SocketShutdown.Send));
        }
        catch { client.Dispose(); throw; }
    }

    private static async Task<List<UsbTarget>> GetTargetsAsync(CancellationToken cancellationToken)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, 27015, cancellationToken).ConfigureAwait(false);
        var response = await ExperimentalIphoneInformationProbe.QueryMuxAsync(client.GetStream(),
            [new XElement("key", "MessageType"), new XElement("string", "ListDevices")], cancellationToken).ConfigureAwait(false);
        return ParseTargets(response);
    }

    internal static List<UsbTarget> ParseTargets(Dictionary<string, XElement> response)
    {
        if (!response.TryGetValue("DeviceList", out var list) || list.Name != "array")
            throw new InvalidDataException("No USB inventory was returned.");
        var entries = list.Elements().Take(257).ToArray();
        if (entries.Length > 256) throw new InvalidDataException("The USB inventory exceeds the limit.");
        List<UsbTarget> targets = [];
        foreach (var entry in entries)
        {
            var item = ExperimentalUsbMuxProbe.ReadDictionary(entry);
            if (!item.TryGetValue("Properties", out var properties)) continue;
            var details = ExperimentalUsbMuxProbe.ReadDictionary(properties);
            if (!details.TryGetValue("ConnectionType", out var type) || type.Name != "string" || type.Value != "USB") continue;
            if (!item.TryGetValue("DeviceID", out var id) || id.Name != "integer" ||
                !int.TryParse(id.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0 ||
                !details.TryGetValue("SerialNumber", out var serial) || serial.Name != "string" || serial.HasElements ||
                serial.Value.Length is < 8 or > 256 || !serial.Value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-'))
                throw new InvalidDataException("The USB target cannot be pinned safely.");
            var bytes = Encoding.UTF8.GetBytes(serial.Value.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant());
            try { targets.Add(new(number, SHA256.HashData(bytes))); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        return targets;
    }

    internal sealed record UsbTarget(int Id, byte[] IdentityHash);
}
