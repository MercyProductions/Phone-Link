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

internal enum MapInboxStage { OpenSession, OpenTelecom, OpenMessages, ReadListing }

internal sealed class MapInboxRejectedException(MapInboxStage stage, PhoneObexRejectedException cause)
    : IOException("The Bluetooth message server rejected a MAP operation.", cause)
{
    public MapInboxStage Stage { get; } = stage;
    public byte Code { get; } = cause.Code;

    public string Describe()
    {
        var operation = Stage switch
        {
            MapInboxStage.OpenSession => "opening the message session",
            MapInboxStage.OpenTelecom => "opening the telecom folder",
            MapInboxStage.OpenMessages => "opening the message folder",
            _ => "reading the inbox preview list",
        };
        var reason = Code == 0xC3 ? "forbade" : "rejected";
        var guidance = Code == 0xC3
            ? " On the iPhone, check Settings > Bluetooth > this PC > ⓘ for sharing options, if offered. Retry with the phone unlocked. USB trust does not grant Bluetooth message access."
            : "";
        return $"The phone {reason} {operation} (OBEX 0x{Code:X2}). No message was sent or changed.{guidance}";
    }
}

internal static class MapInboxClient
{
    private const int MaximumListing = 64 * 1024;
    private const int MaximumItems = 20;
    private static readonly byte[] Target = Convert.FromHexString("BB582B40420C11DBB0DE0800200C9A66");

    internal static async Task<IReadOnlyList<MapInboxItem>> ReadAsync(Stream input, Stream output, bool sendSupportedFeatures, CancellationToken token)
    {
        var obex = new ObexPhoneClient(input, output);
        var stage = MapInboxStage.OpenSession;
        var connected = false;
        try
        {
            await obex.ConnectAsync(Target, sendSupportedFeatures ? [0x29, 4, 0, 0, 0, 4] : [], token).ConfigureAwait(false);
            connected = true;
            stage = MapInboxStage.OpenTelecom;
            await obex.SetExistingFolderAsync("telecom", token).ConfigureAwait(false);
            stage = MapInboxStage.OpenMessages;
            await obex.SetExistingFolderAsync("msg", token).ConfigureAwait(false);
            stage = MapInboxStage.ReadListing;
            return await obex.ReadObjectAsync("inbox", "x-bt/MAP-msg-listing\0"u8.ToArray(),
                [0x01, 2, 0, MaximumItems, 0x03, 1, 0x04, 0x10, 4, 0, 0, 0x10, 0x47, 0x13, 1, 128],
                MaximumListing, Parse, token).ConfigureAwait(false);
        }
        catch (PhoneObexRejectedException cause) { throw new MapInboxRejectedException(stage, cause); }
        finally { if (connected) await obex.DisconnectAsync(token).ConfigureAwait(false); }
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
