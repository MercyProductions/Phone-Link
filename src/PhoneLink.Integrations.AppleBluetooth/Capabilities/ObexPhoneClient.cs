using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace PhoneLink.Integrations.AppleBluetooth.Capabilities;

internal sealed class PhoneObexRejectedException(byte code) : IOException($"The phone returned OBEX status 0x{code:X2}.")
{
    public byte Code { get; } = code;
}

internal sealed class ObexPhoneClient(Stream input, Stream output)
{
    private const int MaximumPacket = 4096;
    private byte[]? _connection;

    public async Task ConnectAsync(byte[] target, byte[] parameters, CancellationToken token)
    {
        var connected = await ExchangeAsync(0x80,
            [0x10, 0, 0x10, 0x00, .. Variable(0x46, target), .. Variable(0x4C, parameters)], token).ConfigureAwait(false);
        RequireSuccess(connected.Code);
        if (connected.Payload.Length < 4 || (connected.Payload[0] & 0xf0) != 0x10 || connected.Payload[1] != 0 ||
            BinaryPrimitives.ReadUInt16BigEndian(connected.Payload.AsSpan(2, 2)) < 255)
            throw new InvalidDataException("The OBEX connection response was malformed.");
        var headers = Headers(connected.Payload[4..]).ToArray();
        var who = headers.Where(item => item.Kind == 0x4A).ToArray();
        var ids = headers.Where(item => item.Kind == 0xCB).ToArray();
        if (headers.Any(item => item.Kind is 0x4D or 0x4E) || who.Length != 1 ||
            !who[0].Value.AsSpan().SequenceEqual(target) || ids.Length != 1)
            throw new InvalidDataException("The phone did not confirm an unambiguous OBEX service session.");
        _connection = [0xCB, .. ids[0].Value];
    }

    public async Task SetExistingFolderAsync(string folder, CancellationToken token) =>
        RequireSuccess((await ExchangeAsync(0x85, [0x02, 0, .. Connection, .. Name(folder)], token).ConfigureAwait(false)).Code);

    public async Task<ushort> ReadPhoneBookSizeAsync(CancellationToken token)
    {
        var response = await ExchangeAsync(0x83, [.. Connection, .. Name("telecom/pb.vcf"),
            .. Variable(0x42, "x-bt/phonebook\0"u8), .. Variable(0x4C, [0x04, 2, 0, 0])], token).ConfigureAwait(false);
        try
        {
            if (response.Code == 0x90) throw new InvalidDataException("Unexpected continuation for a count-only request.");
            RequireSuccess(response.Code);
            ushort? count = null;
            var sawParameters = false;
            foreach (var (kind, value) in Headers(response.Payload))
            {
                try
                {
                    if (kind is 0x48 or 0x49) throw new InvalidDataException("The count-only response included a body.");
                    if (kind != 0x4C) continue;
                    if (sawParameters) throw new InvalidDataException("Duplicate application parameter headers.");
                    sawParameters = true;
                    for (var offset = 0; offset < value.Length;)
                    {
                        if (value.Length - offset < 2 || value[offset + 1] > value.Length - offset - 2)
                            throw new InvalidDataException("The phone-book size parameter was truncated.");
                        var length = value[offset + 1];
                        if (value[offset] == 0x08)
                        {
                            if (count is not null || length != 2) throw new InvalidDataException("Ambiguous phone-book size.");
                            count = BinaryPrimitives.ReadUInt16BigEndian(value.AsSpan(offset + 2, 2));
                        }
                        offset += 2 + length;
                    }
                }
                finally { CryptographicOperations.ZeroMemory(value); }
            }
            return count ?? throw new InvalidDataException("The phone did not report a phone-book size.");
        }
        finally { CryptographicOperations.ZeroMemory(response.Payload); }
    }

    public async Task<T> ReadObjectAsync<T>(string name, byte[] type, byte[] parameters, int maximumBody,
        Func<Stream, T> parse, CancellationToken token)
    {
        byte[] get = [.. Connection, .. Name(name), .. Variable(0x42, type), .. Variable(0x4C, parameters)];
        using var body = new MemoryStream(maximumBody);
        try
        {
            for (var round = 0; round < 64; round++)
            {
                var response = await ExchangeAsync(0x83, round == 0 ? get : Connection, token).ConfigureAwait(false);
                try
                {
                    if (response.Code != 0x90) RequireSuccess(response.Code);
                    var end = false;
                    foreach (var (kind, value) in Headers(response.Payload))
                    {
                        try
                        {
                            if (kind is not (0x48 or 0x49)) continue;
                            if (end || body.Length + value.Length > maximumBody) throw new InvalidDataException("The OBEX object exceeded its limits.");
                            body.Write(value);
                            end = kind == 0x49;
                        }
                        finally { CryptographicOperations.ZeroMemory(value); }
                    }
                    if (response.Code == 0x90 && !end) continue;
                    if (response.Code != 0xA0 || !end) throw new InvalidDataException("The OBEX object ended without a complete body.");
                    body.Position = 0;
                    return parse(body);
                }
                finally { CryptographicOperations.ZeroMemory(response.Payload); }
            }
            throw new InvalidDataException("Too many OBEX continuation packets.");
        }
        finally { CryptographicOperations.ZeroMemory(body.GetBuffer()); }
    }

    public async Task DisconnectAsync(CancellationToken token)
    {
        if (_connection is null || token.IsCancellationRequested) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        try { await ExchangeAsync(0x81, Connection, deadline.Token).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or OperationCanceledException) { }
        finally { _connection = null; }
    }

    private byte[] Connection => _connection ?? throw new InvalidOperationException("Connect before issuing an OBEX request.");
    private static byte[] Name(string value) => Variable(0x01, Encoding.BigEndianUnicode.GetBytes(value + '\0'));

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

    private async Task<(byte Code, byte[] Payload)> ExchangeAsync(byte code, byte[] body, CancellationToken token)
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
        if (code != 0xA0) throw new PhoneObexRejectedException(code);
    }
}
