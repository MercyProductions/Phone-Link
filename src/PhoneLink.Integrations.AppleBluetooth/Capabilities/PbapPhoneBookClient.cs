using System.Text;
using System.Globalization;

namespace PhoneLink.Integrations.AppleBluetooth.Capabilities;

public enum BluetoothCallHistoryFolder { Combined, Incoming, Outgoing }

public sealed class BluetoothContactPreview(string name, IReadOnlyList<string> phoneNumbers)
{
    public string Name { get; } = name;
    public IReadOnlyList<string> PhoneNumbers { get; } = phoneNumbers;
    public string NumbersText => string.Join(" · ", PhoneNumbers);
}

public sealed class BluetoothCallPreview(string name, string number, string direction, string phoneTime)
{
    public string Name { get; } = name;
    public string Number { get; } = number;
    public string Direction { get; } = direction;
    public string PhoneTime { get; } = phoneTime;
}

internal static class PbapPhoneBookClient
{
    internal const ushort PageSize = 100;
    internal const byte CallHistoryLimit = 30;
    private const int MaximumLine = 4096;
    private static readonly byte[] Target = Convert.FromHexString("796135F0F0C511D809660800200C9A66");

    internal static async Task<ushort?> ReadSizeAsync(Stream input, Stream output, bool includeFeatures, CancellationToken token)
    {
        var obex = new ObexPhoneClient(input, output);
        await obex.ConnectAsync(Target, includeFeatures ? [0x10, 4, 0, 0, 0, 1] : [], token).ConfigureAwait(false);
        try { return await obex.ReadPhoneBookSizeAsync(token).ConfigureAwait(false); }
        finally { await obex.DisconnectAsync(token).ConfigureAwait(false); }
    }

    internal static Task<IReadOnlyList<BluetoothContactPreview>> ReadAsync(Stream input, Stream output,
        ushort startOffset, bool includeFeatures, CancellationToken token) => ReadObjectAsync(input, output, "telecom/pb.vcf",
            [0x04, 2, 0, (byte)PageSize, 0x05, 2, (byte)(startOffset >> 8), (byte)startOffset,
                0x06, 8, 0, 0, 0, 0, 0, 0, 0, 0x87, 0x07, 1, 1], includeFeatures, Parse, token);

    internal static Task<IReadOnlyList<BluetoothCallPreview>> ReadCallHistoryAsync(Stream input, Stream output,
        BluetoothCallHistoryFolder folder, bool includeFeatures, CancellationToken token) =>
        ReadObjectAsync(input, output, GetCallHistoryPath(folder),
            [0x04, 2, 0, CallHistoryLimit, 0x06, 8, 0, 0, 0, 0, 0x10, 0, 0, 0x87, 0x07, 1, 1],
            includeFeatures, ParseCallHistory, token);

    internal static string GetCallHistoryPath(BluetoothCallHistoryFolder folder) => folder switch
    {
        BluetoothCallHistoryFolder.Combined => "telecom/cch.vcf",
        BluetoothCallHistoryFolder.Incoming => "telecom/ich.vcf",
        BluetoothCallHistoryFolder.Outgoing => "telecom/och.vcf",
        _ => throw new ArgumentOutOfRangeException(nameof(folder)),
    };

    private static async Task<T> ReadObjectAsync<T>(Stream input, Stream output, string path, byte[] parameters,
        bool includeFeatures, Func<Stream, T> parse, CancellationToken token)
    {
        var obex = new ObexPhoneClient(input, output);
        await obex.ConnectAsync(Target, includeFeatures ? [0x10, 4, 0, 0, 0, 1] : [], token).ConfigureAwait(false);
        try
        {
            return await obex.ReadObjectAsync(path, "x-bt/phonebook\0"u8.ToArray(), parameters,
                128 * 1024, parse, token).ConfigureAwait(false);
        }
        finally { await obex.DisconnectAsync(token).ConfigureAwait(false); }
    }

    internal static IReadOnlyList<BluetoothContactPreview> Parse(Stream input) =>
        ParseCards(input, PageSize, static card => new BluetoothContactPreview(card.Name ?? "Unnamed contact", card.Numbers));

    internal static IReadOnlyList<BluetoothCallPreview> ParseCallHistory(Stream input) =>
        ParseCards(input, CallHistoryLimit, static card => new BluetoothCallPreview(card.Name ?? "Unknown caller",
            card.Numbers.Count == 0 ? "Number unavailable" : card.Numbers[0],
            card.Direction ?? "Direction unavailable", card.PhoneTime ?? "Time unavailable"));

    private static List<T> ParseCards<T>(Stream input, int maximum, Func<Card, T> project)
    {
        using var reader = new StreamReader(input, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        List<T> rows = [];
        var inCard = false;
        string? version = null, name = null, structuredName = null, direction = null, phoneTime = null, line = null;
        List<string> numbers = [];
        while (reader.ReadLine() is { } physical)
        {
            if (physical.Length > MaximumLine) throw Invalid();
            if (physical.Length > 0 && physical[0] is ' ' or '\t')
            {
                if (line is null || line.Length + physical.Length - 1 > MaximumLine) throw Invalid();
                line += physical[1..];
                continue;
            }
            if (line is not null) Process(line);
            line = physical;
        }
        if (line is not null) Process(line);
        if (inCard) throw Invalid();
        return rows;

        void Process(string value)
        {
            if (value.Length == 0) return;
            if (value.Equals("BEGIN:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                if (inCard || rows.Count >= maximum) throw Invalid();
                inCard = true;
                version = name = structuredName = direction = phoneTime = null;
                numbers.Clear();
                return;
            }
            if (!inCard) throw Invalid();
            if (value.Equals("END:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                if (version != "3.0") throw Invalid();
                rows.Add(project(new(!string.IsNullOrWhiteSpace(name) ? name :
                    !string.IsNullOrWhiteSpace(structuredName) ? structuredName : null, numbers.ToArray(), direction, phoneTime)));
                inCard = false;
                return;
            }
            var separator = value.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0) throw Invalid();
            var property = value[..separator];
            var key = property.Split(';', 2)[0].Split('.').Last().ToUpperInvariant();
            var raw = value[(separator + 1)..];
            if (key == "VERSION") { if (version is not null) throw Invalid(); version = raw; return; }
            if (property.Contains("ENCODING=", StringComparison.OrdinalIgnoreCase)) return;
            if (key == "FN") name ??= Clean(raw, 180, structured: false);
            else if (key == "N") structuredName ??= Clean(raw, 180, structured: true);
            else if (key == "X-IRMC-CALL-DATETIME")
            {
                direction ??= ParseDirection(property);
                if (DateTime.TryParseExact(raw, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                    phoneTime ??= time.ToString("yyyy-MM-dd HH:mm:ss 'phone time'", CultureInfo.InvariantCulture);
            }
            else if (key == "TEL" && numbers.Count < 8)
            {
                var number = Clean(raw, 72, structured: false);
                if (number.Length > 0 && !numbers.Contains(number, StringComparer.Ordinal)) numbers.Add(number);
            }
        }
    }

    private static string? ParseDirection(string property)
    {
        foreach (var parameter in property.Split(';').Skip(1))
        {
            if (!parameter.StartsWith("TYPE=", StringComparison.OrdinalIgnoreCase)) continue;
            return parameter[5..].Trim('"').ToUpperInvariant() switch
            {
                "MISSED" => "Missed", "RECEIVED" => "Received", "DIALED" => "Dialed", _ => null,
            };
        }
        return null;
    }

    private static string Clean(string value, int limit, bool structured)
    {
        var text = new StringBuilder(Math.Min(value.Length, limit));
        for (var i = 0; i < value.Length && text.Length < limit; i++)
        {
            var ch = value[i];
            if (ch == '\\' && i + 1 < value.Length)
            {
                ch = value[++i];
                if (ch is 'n' or 'N') ch = ' ';
            }
            else if (ch == ';' && structured) ch = ' ';
            if (!char.IsControl(ch)) text.Append(ch);
        }
        return text.ToString().Trim();
    }

    private static InvalidDataException Invalid() => new("The phone returned an incomplete or unsupported vCard 3.0 page.");
    private sealed record Card(string? Name, IReadOnlyList<string> Numbers, string? Direction, string? PhoneTime);
}
