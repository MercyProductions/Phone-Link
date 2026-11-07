using System.Text;
using PhoneLink.Integrations.AppleBluetooth.Capabilities;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class PbapPhoneBookClientTests
{
    private static readonly byte[] ConnectResponse = Convert.FromHexString("A0001F100010004A0013796135F0F0C511D809660800200C9A66CB01020304");
    private static readonly string[] ExpectedNumbers = ["123", "456"];

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
}
