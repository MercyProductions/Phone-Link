using System.Globalization;
using System.Net;

namespace PhoneLink.DeviceCommunication.Discovery;

public sealed record BonjourServiceObservation(
    string Id,
    string DisplayName,
    string ServiceType,
    string HostName,
    int? Port,
    IReadOnlyList<string> Addresses,
    DateTimeOffset ObservedAt)
{
    public bool IsIdentityVerified { get; }

    public string Description => ServiceType switch
    {
        "_phonelink._tcp" => "Phone Link service advertisement",
        "_airplay._tcp" => "AirPlay service advertisement",
        "_raop._tcp" => "Network audio service advertisement",
        _ => "Local service advertisement",
    };

    public static BonjourServiceObservation? FromProperties(
        string id,
        string fallbackName,
        IReadOnlyDictionary<string, object> properties,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(properties);
        var serviceType = Text(properties, "System.Devices.Dnssd.ServiceName", 100).TrimEnd('.').ToLowerInvariant();
        if (serviceType is not ("_phonelink._tcp" or "_airplay._tcp" or "_raop._tcp"))
        {
            return null;
        }

        var name = Text(properties, "System.Devices.Dnssd.InstanceName", 200);
        var host = Text(properties, "System.Devices.Dnssd.HostName", 253);
        int? port = null;
        if (properties.TryGetValue("System.Devices.Dnssd.PortNumber", out var rawPort) &&
            rawPort is ushort or uint or int &&
            long.TryParse(Convert.ToString(rawPort, CultureInfo.InvariantCulture), out var parsedPort) &&
            parsedPort is >= 1 and <= 65_535)
        {
            port = (int)parsedPort;
        }

        IEnumerable<string> candidates = properties.TryGetValue("System.Devices.IpAddress", out var rawAddresses)
            ? rawAddresses switch
            {
                string value => [value],
                IEnumerable<string> values => values,
                _ => [],
            }
            : [];
        var addresses = candidates.Take(32)
            .Select(value => IPAddress.TryParse(value, out var address) ? address.ToString() : null)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToArray();

        return new BonjourServiceObservation(
            id,
            string.IsNullOrWhiteSpace(name) ? Sanitize(fallbackName, 200) : name,
            serviceType,
            host,
            port,
            addresses,
            observedAt);
    }

    private static string Text(IReadOnlyDictionary<string, object> properties, string key, int limit) =>
        properties.TryGetValue(key, out var value) && value is string text ? Sanitize(text, limit) : "";

    private static string Sanitize(string value, int limit) =>
        new(value.Where(character => !char.IsControl(character) &&
            char.GetUnicodeCategory(character) != UnicodeCategory.Format).Take(limit).ToArray());
}
