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

internal static class MapInboxClient
{
    private const int MaximumListing = 64 * 1024;
    private const int MaximumItems = 20;
    private static readonly byte[] Target = Convert.FromHexString("BB582B40420C11DBB0DE0800200C9A66");

    internal static async Task<IReadOnlyList<MapInboxItem>> ReadAsync(Stream input, Stream output, bool sendSupportedFeatures, CancellationToken token)
    {
        var obex = new ObexPhoneClient(input, output);
        await obex.ConnectAsync(Target, sendSupportedFeatures ? [0x29, 4, 0, 0, 0, 4] : [], token).ConfigureAwait(false);
        try
        {
            await obex.SetExistingFolderAsync("telecom", token).ConfigureAwait(false);
            await obex.SetExistingFolderAsync("msg", token).ConfigureAwait(false);
            return await obex.ReadObjectAsync("inbox", "x-bt/MAP-msg-listing\0"u8.ToArray(),
                [0x01, 2, 0, MaximumItems, 0x03, 1, 0x04, 0x10, 4, 0, 0, 0x10, 0x47, 0x13, 1, 128],
                MaximumListing, Parse, token).ConfigureAwait(false);
        }
        finally { await obex.DisconnectAsync(token).ConfigureAwait(false); }
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

}
