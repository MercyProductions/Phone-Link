using System.Text;
using PhoneLink.Integrations.AppleBluetooth.Capabilities;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class PbapPhoneBookClientTests
{
    private static readonly byte[] ConnectResponse = Convert.FromHexString("A0001F100010004A0013796135F0F0C511D809660800200C9A66CB01020304");
    private static readonly string[] ExpectedNumbers = ["123", "456"];

    [Fact]
    public async Task SuccessfulEmptyCombinedCallLogIsNotAClaimThatRecentsAreEmpty()
    {
        await using var input = new MemoryStream([.. ConnectResponse, 0xA0, 0, 6, 0x49, 0, 3, 0xA0, 0, 3]);
        await using var output = new MemoryStream();
        var entries = await PbapPhoneBookClient.ReadCallHistoryAsync(input, output, true, CancellationToken.None);
        var result = BluetoothCallHistoryObservation.FromPermittedEntries(entries);
        Assert.True(result.Completed);
        Assert.Empty(result.Items);
        Assert.Contains("shared no entries", result.Status);
        Assert.Contains("does not mean the iPhone’s Recents list is empty", result.Status);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0x12, 0x34, 0x1234)]
    public async Task CountOnlyRequestsNoContactBodyAndReadsBigEndianSize(byte high, byte low, ushort expected)
    {
        await using var input = new MemoryStream([.. ConnectResponse, 0xA0, 0, 10, 0x4C, 0, 7,
            0x08, 2, high, low, 0xA0, 0, 3]);
        await using var output = new MemoryStream();
        Assert.Equal(expected, await PbapPhoneBookClient.ReadSizeAsync(input, output, true, CancellationToken.None));
        var wire = output.ToArray();
        var get = (wire[1] << 8) + wire[2];
        var end = get + (wire[get + 1] << 8) + wire[get + 2];
        Assert.Equal(0x83, wire[get]);
        Assert.True(wire.AsSpan(get, end - get).EndsWith(new byte[] { 0x4C, 0, 7, 0x04, 2, 0, 0 }));
        Assert.True(wire.AsSpan(get, end - get).IndexOf(Encoding.BigEndianUnicode.GetBytes("telecom/pb.vcf\0")) >= 0);
        Assert.Equal(0x81, wire[end]);
        Assert.Equal(wire.Length, end + (wire[end + 1] << 8) + wire[end + 2]);
    }

    [Theory]
    [InlineData("A00003")] // no parameters
    [InlineData("A0000A4C000708030001")] // truncated TLV
    [InlineData("A0000E4C000B0802000108020002")] // duplicate count
    [InlineData("A0000E4C00070802000149000458")] // unsolicited body
    [InlineData("900003")] // no continuation needed or allowed
    public async Task MalformedCountReplyIsRejectedWithoutFallback(string reply)
    {
        await using var input = new MemoryStream([.. ConnectResponse, .. Convert.FromHexString(reply), 0xA0, 0, 3]);
        await using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PbapPhoneBookClient.ReadSizeAsync(input, output, false, CancellationToken.None));
        var wire = output.ToArray();
        var get = (wire[1] << 8) + wire[2];
        var end = get + (wire[get + 1] << 8) + wire[get + 2];
        Assert.Equal(0x81, wire[end]);
        Assert.Equal(wire.Length, end + (wire[end + 1] << 8) + wire[end + 2]);
    }

    [Fact]
    public async Task CountDenialIsNotReportedAsZero()
    {
        await using var input = new MemoryStream([.. ConnectResponse, 0xC3, 0, 3, 0xA0, 0, 3]);
        await using var output = new MemoryStream();
        var error = await Assert.ThrowsAsync<PhoneObexRejectedException>(() =>
            PbapPhoneBookClient.ReadSizeAsync(input, output, false, CancellationToken.None));
        Assert.Equal(0xC3, error.Code);
    }

    [Fact]
    public async Task PullsOnlyTheRequestedContactPageAndFieldMask()
    {
        var vcard = "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:José\\, Test\r\nTEL;TYPE=CELL:+1555\r\nEND:VCARD\r\n"u8.ToArray();
        await using var input = new MemoryStream([.. ConnectResponse, 0xA0, 0, (byte)(vcard.Length + 6),
            0x49, 0, (byte)(vcard.Length + 3), .. vcard, 0xA0, 0, 3]);
        await using var output = new MemoryStream();
        var row = Assert.Single(await PbapPhoneBookClient.ReadAsync(input, output, 100, true, CancellationToken.None));
        Assert.Equal("José, Test", row.Name);
        Assert.Equal("+1555", Assert.Single(row.PhoneNumbers));
        var wire = output.ToArray();
        var get = (wire[1] << 8) + wire[2];
        Assert.Equal(0x80, wire[0]);
        Assert.Equal(0x83, wire[get]);
        Assert.Equal(0x81, wire[get + (wire[get + 1] << 8) + wire[get + 2]]);
        Assert.True(wire.AsSpan().IndexOf(Encoding.BigEndianUnicode.GetBytes("telecom/pb.vcf\0")) >= 0);
        Assert.True(wire.AsSpan().IndexOf(new byte[] { 0x05, 2, 0, 100, 0x06, 8, 0, 0, 0, 0, 0, 0, 0, 0x87 }) >= 0);
    }

    [Fact]
    public void UnfoldsNamesAndNeverProjectsOtherContactFields()
    {
        using var input = new MemoryStream("BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Long \r\n Name\r\nitem1.TEL;TYPE=HOME:123\r\nTEL:123\r\nTEL:456\r\nEMAIL:ignored@example.test\r\nNOTE:ignored\r\nEND:VCARD\r\n"u8.ToArray());
        var row = Assert.Single(PbapPhoneBookClient.Parse(input));
        Assert.Equal("Long Name", row.Name);
        Assert.Equal(ExpectedNumbers, row.PhoneNumbers);
    }

    [Theory]
    [InlineData("BEGIN:VCARD\nVERSION:3.0\nFN:Incomplete")]
    [InlineData("BEGIN:VCARD\nVERSION:2.1\nFN:Wrong version\nEND:VCARD")]
    [InlineData("BEGIN:VCARD\nBEGIN:VCARD\nVERSION:3.0\nEND:VCARD")]
    public void RejectsTruncatedOrUnsupportedPages(string value)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(value));
        Assert.Throws<InvalidDataException>(() => PbapPhoneBookClient.Parse(input));
    }

    [Fact]
    public async Task ReadsCombinedCallLogWithoutRequestingMissedCallReset()
    {
        var vcard = "BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Test Caller\r\nTEL:+1555\r\nX-IRMC-CALL-DATETIME;TYPE=MISSED:20261107T120000\r\nEND:VCARD\r\n"u8.ToArray();
        await using var input = new MemoryStream([.. ConnectResponse, 0xA0, 0, (byte)(vcard.Length + 6),
            0x49, 0, (byte)(vcard.Length + 3), .. vcard, 0xA0, 0, 3]);
        await using var output = new MemoryStream();
        var row = Assert.Single(await PbapPhoneBookClient.ReadCallHistoryAsync(input, output, true, CancellationToken.None));
        Assert.Equal("Missed", row.Direction);
        Assert.Equal("2026-11-07 12:00:00 phone time", row.PhoneTime);
        Assert.Equal("+1555", row.Number);
        var wire = output.ToArray();
        Assert.True(wire.AsSpan().IndexOf(Encoding.BigEndianUnicode.GetBytes("telecom/cch.vcf\0")) >= 0);
        Assert.True(wire.AsSpan().IndexOf(new byte[] { 0x4C, 0, 20, 0x04, 2, 0, 30, 0x06, 8, 0, 0, 0, 0, 0x10, 0, 0, 0x87, 0x07, 1, 1 }) >= 0);
    }

    [Theory]
    [InlineData("RECEIVED", "", "Received")]
    [InlineData("DIALED", "not-a-time", "Dialed")]
    [InlineData("UNKNOWN", "20260230T010101", "Direction unavailable")]
    public void DoesNotGuessMissingOrInvalidCallTime(string kind, string timestamp, string direction)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes($"BEGIN:VCARD\nVERSION:3.0\nX-IRMC-CALL-DATETIME;TYPE={kind}:{timestamp}\nEND:VCARD"));
        var row = Assert.Single(PbapPhoneBookClient.ParseCallHistory(input));
        Assert.Equal(direction, row.Direction);
        Assert.Equal("Time unavailable", row.PhoneTime);
        Assert.Equal("Number unavailable", row.Number);
    }
}
