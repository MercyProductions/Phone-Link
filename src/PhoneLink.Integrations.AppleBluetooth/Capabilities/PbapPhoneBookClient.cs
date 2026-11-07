using System.Text;

namespace PhoneLink.Integrations.AppleBluetooth.Capabilities;

public sealed class BluetoothContactPreview(string name, IReadOnlyList<string> phoneNumbers)
{
    public string Name { get; } = name;
    public IReadOnlyList<string> PhoneNumbers { get; } = phoneNumbers;
    public string NumbersText => string.Join(" · ", PhoneNumbers);
}

internal static class PbapPhoneBookClient
{
    internal const ushort PageSize = 100;
    private const int MaximumLine = 4096;
    private static readonly byte[] Target = Convert.FromHexString("796135F0F0C511D809660800200C9A66");

    internal static async Task<IReadOnlyList<BluetoothContactPreview>> ReadAsync(Stream input, Stream output,
        ushort startOffset, bool includeFeatures, CancellationToken token)
    {
        var obex = new ObexPhoneClient(input, output);
        await obex.ConnectAsync(Target, includeFeatures ? [0x10, 4, 0, 0, 0, 1] : [], token).ConfigureAwait(false);
        try
        {
            byte[] parameters = [0x04, 2, 0, (byte)PageSize, 0x05, 2, (byte)(startOffset >> 8), (byte)startOffset,
                0x06, 8, 0, 0, 0, 0, 0, 0, 0, 0x87, 0x07, 1, 1];
            return await obex.ReadObjectAsync("telecom/pb.vcf", "x-bt/phonebook\0"u8.ToArray(), parameters,
                128 * 1024, Parse, token).ConfigureAwait(false);
        }
        finally { await obex.DisconnectAsync(token).ConfigureAwait(false); }
    }

    internal static IReadOnlyList<BluetoothContactPreview> Parse(Stream input)
    {
        using var reader = new StreamReader(input, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        List<BluetoothContactPreview> rows = [];
        var inCard = false;
        string? version = null, name = null, structuredName = null, line = null;
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
                if (inCard || rows.Count >= PageSize) throw Invalid();
                inCard = true;
                version = name = structuredName = null;
                numbers.Clear();
                return;
            }
            if (!inCard) throw Invalid();
            if (value.Equals("END:VCARD", StringComparison.OrdinalIgnoreCase))
            {
                if (version != "3.0") throw Invalid();
                rows.Add(new(string.IsNullOrWhiteSpace(name) ? structuredName ?? "Unnamed contact" : name, numbers.ToArray()));
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
            else if (key == "TEL" && numbers.Count < 8)
            {
                var number = Clean(raw, 72, structured: false);
                if (number.Length > 0 && !numbers.Contains(number, StringComparer.Ordinal)) numbers.Add(number);
            }
        }
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
}
