using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace PhoneLink.DeviceCommunication.Diagnostics;

public sealed record IphoneBasicInformation(string? DeviceClass, string? ProductType,
    string? IosVersion, string? BuildVersion, int? BatteryPercentage, bool? IsCharging,
    bool? ExternalPowerConnected, int UnavailableFields);

public static class ExperimentalIphoneInformationProbe
{
    // Only these non-unique read-only keys are requested. No all-values query or private-key access.
    private static readonly string[] Keys = ["DeviceClass", "ProductType", "ProductVersion", "BuildVersion"];
    private static readonly string[] BatteryKeys = ["BatteryCurrentCapacity", "BatteryIsCharging", "ExternalConnected"];

    public static async Task<IphoneBasicInformation> ProbeAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        using var connection = await OpenSingleUsbChannelAsync(62078, timeout.Token).ConfigureAwait(false);
        var stream = connection.GetStream();

        var values = new string?[Keys.Length];
        for (var index = 0; index < Keys.Length; index++)
        {
            var payload = await ReadValuePayloadAsync(stream, Keys[index], null, timeout.Token).ConfigureAwait(false);
            try
            {
                values[index] = ParseReadResponse(payload);
            }
            finally { CryptographicOperations.ZeroMemory(payload); }
        }
        var battery = new XElement?[BatteryKeys.Length];
        for (var index = 0; index < BatteryKeys.Length; index++)
        {
            var payload = await ReadValuePayloadAsync(stream, BatteryKeys[index], "com.apple.mobile.battery", timeout.Token).ConfigureAwait(false);
            try { battery[index] = GetReadValue(payload); }
            finally { CryptographicOperations.ZeroMemory(payload); }
        }
        var percentage = ParseBatteryPercentage(battery[0]);
        var charging = ParseFlag(battery[1]);
        var external = ParseFlag(battery[2]);
        return new(values[0], values[1], values[2], values[3], percentage, charging, external,
            values.Count(value => value is null) + (percentage is null ? 1 : 0) + (charging is null ? 1 : 0) + (external is null ? 1 : 0));
    }

    internal static async Task<TcpClient> OpenSingleUsbChannelAsync(ushort port, CancellationToken cancellationToken)
    {
        if (port is not (22 or 62078)) throw new ArgumentOutOfRangeException(nameof(port));
        using var listClient = new TcpClient(AddressFamily.InterNetwork);
        await listClient.ConnectAsync(IPAddress.Loopback, 27015, cancellationToken).ConfigureAwait(false);
        var list = await QueryMuxAsync(listClient.GetStream(),
            [new XElement("key", "MessageType"), new XElement("string", "ListDevices")], cancellationToken).ConfigureAwait(false);
        var deviceId = SelectSingleUsbDevice(list);
        var connection = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            await connection.ConnectAsync(IPAddress.Loopback, 27015, cancellationToken).ConfigureAwait(false);
            var response = await QueryMuxAsync(connection.GetStream(),
                [new XElement("key", "MessageType"), new XElement("string", "Connect"),
                 new XElement("key", "DeviceID"), new XElement("integer", deviceId),
                 new XElement("key", "PortNumber"), new XElement("integer", BinaryPrimitives.ReverseEndianness(port))],
                cancellationToken).ConfigureAwait(false);
            if (!response.TryGetValue("MessageType", out var message) || message.Name != "string" || message.Value != "Result" ||
                !response.TryGetValue("Number", out var number) || number.Name != "integer" ||
                !int.TryParse(number.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var code))
                throw new InvalidDataException("The local transport returned an unsupported channel response.");
            if (code != 0) throw new UsbMuxChannelUnavailableException(code);
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    public static byte[] BuildReadRequest(string key, string? domain = null)
    {
        if (!(domain is null && Keys.Contains(key, StringComparer.Ordinal) ||
            domain == "com.apple.mobile.battery" && BatteryKeys.Contains(key, StringComparer.Ordinal)))
            throw new ArgumentException("This information domain/key is not allowlisted.", nameof(key));
        return Encode([new XElement("key", "Label"), new XElement("string", "PhoneLink-Experimental"),
            new XElement("key", "Request"), new XElement("string", "GetValue"),
            .. domain is null ? Array.Empty<XElement>() : [new XElement("key", "Domain"), new XElement("string", domain)],
            new XElement("key", "Key"), new XElement("string", key)]);
    }

    public static string? ParseReadResponse(byte[] payload)
    {
        var value = GetReadValue(payload);
        if (value is null || value.Name != "string" || value.HasElements ||
            value.Value.Length is 0 or > 64 || !value.Value.All(character => char.IsAsciiLetterOrDigit(character) || character is ',' or '.' or '-'))
            return null;
        return value.Value;
    }

    private static XElement? GetReadValue(byte[] payload)
    {
        var response = ExperimentalUsbMuxProbe.ParseDictionary(payload);
        if (!response.TryGetValue("Request", out var request) || request.Name != "string" || request.HasElements || request.Value != "GetValue")
            throw new InvalidDataException("The information response is not correlated to GetValue.");
        if (response.ContainsKey("Error")) return null;
        return response.GetValueOrDefault("Value");
    }

    public static int? ParseBatteryPercentage(XElement? value) =>
        value is not null && value.Name == "integer" && !value.HasElements &&
        int.TryParse(value.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var percentage) && percentage is >= 0 and <= 100
            ? percentage : null;

    public static bool? ParseFlag(XElement? value) => value is null || value.Name.Namespace != XNamespace.None || value.HasElements || !string.IsNullOrWhiteSpace(value.Value)
        ? null : value.Name.LocalName switch { "true" => true, "false" => false, _ => null };

    private static async Task<byte[]> ReadValuePayloadAsync(Stream stream, string key, string? domain, CancellationToken cancellationToken)
    {
        var request = BuildReadRequest(key, domain);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, request.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length is 0 or > 65_536) throw new InvalidDataException("The information response exceeds the limit.");
        var payload = new byte[checked((int)length)];
        try { await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false); return payload; }
        catch { CryptographicOperations.ZeroMemory(payload); throw; }
    }

    private static int SelectSingleUsbDevice(Dictionary<string, XElement> response)
    {
        if (!response.TryGetValue("DeviceList", out var list) || list.Name != "array")
            throw new InvalidDataException("No transport inventory was returned.");
        List<int> ids = [];
        var entries = list.Elements().Take(257).ToArray();
        if (entries.Length > 256) throw new InvalidDataException("The transport inventory exceeds the limit.");
        foreach (var entry in entries)
        {
            var values = ExperimentalUsbMuxProbe.ReadDictionary(entry);
            if (!values.TryGetValue("Properties", out var properties)) continue;
            var details = ExperimentalUsbMuxProbe.ReadDictionary(properties);
            if (!details.TryGetValue("ConnectionType", out var type) || type.Name != "string" || type.HasElements || type.Value != "USB") continue;
            if (!values.TryGetValue("DeviceID", out var id) || id.Name != "integer" ||
                !int.TryParse(id.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                throw new InvalidDataException("The transport device identifier is invalid.");
            ids.Add(parsed);
        }
        if (ids.Count != 1)
            throw new InvalidOperationException("Connect exactly one owned iPhone over USB for this experimental information check.");
        return ids[0];
    }

    internal static async Task<Dictionary<string, XElement>> QueryMuxAsync(Stream stream,
        XElement[] fields, CancellationToken cancellationToken)
    {
        var payload = await ExperimentalUsbMuxProbe.ExchangePayloadAsync(stream, Encode(fields), cancellationToken).ConfigureAwait(false);
        try { return ExperimentalUsbMuxProbe.ParseDictionary(payload); }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private static byte[] Encode(XElement[] fields) => Encoding.UTF8.GetBytes(
        new XDocument(new XElement("plist", new XAttribute("version", "1.0"), new XElement("dict", fields)))
            .ToString(SaveOptions.DisableFormatting));
}

public sealed class UsbMuxChannelUnavailableException(int responseCode) : IOException("The device transport rejected the requested channel.")
{
    public int ResponseCode { get; } = responseCode;
}
