using System.Text;
using System.Xml;
using PhoneLink.Integrations.AppleBluetooth.Capabilities;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class MapInboxClientTests
{
    private static readonly byte[] ConnectResponse = Convert.FromHexString("A0001F100010004A0013BB582B40420C11DBB0DE0800200C9A66CB01020304");

    [Fact]
    public async Task ReadsOnlyInboxOverBoundedObexAndFollowsContinuation()
    {
        var listing = Encoding.UTF8.GetBytes("<MAP-msg-listing version=\"1.0\"><msg type=\"SMS_GSM\" sender_name=\"Test\" subject=\"Hello\"/></MAP-msg-listing>");
        await using var input = new MemoryStream([.. ConnectResponse, 0xA0, 0, 3, 0xA0, 0, 3,
            .. Body(0x90, 0x48, listing[..10]), .. Body(0xA0, 0x49, listing[10..]), 0xA0, 0, 3]);
        await using var output = new MemoryStream();
        var row = Assert.Single(await MapInboxClient.ReadAsync(input, output, true, CancellationToken.None));
        Assert.Equal("Hello", row.Preview);
        Assert.Equal("Test", row.Sender);
        var wire = output.ToArray();
        List<byte> commands = [];
        for (var offset = 0; offset < wire.Length; offset += (wire[offset + 1] << 8) + wire[offset + 2])
            commands.Add(wire[offset]);
        Assert.Equal(new byte[] { 0x80, 0x85, 0x85, 0x83, 0x83, 0x81 }, commands);
        Assert.Contains("x-bt/MAP-msg-listing", Encoding.ASCII.GetString(wire), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ObexDenialDoesNotProceedToInbox()
    {
        await using var input = new MemoryStream([0xC1, 0, 3]);
        await using var output = new MemoryStream();
        var error = await Assert.ThrowsAsync<MapObexRejectedException>(() => MapInboxClient.ReadAsync(input, output, false, CancellationToken.None));
        Assert.Equal(0xC1, error.Code);
        var wire = output.ToArray();
        Assert.Equal(wire.Length, (wire[1] << 8) + wire[2]);
    }

    [Fact]
    public void DoesNotResolveExternalEntitiesOrExposeEmail()
    {
        using var malicious = new MemoryStream("<!DOCTYPE MAP-msg-listing [<!ENTITY read SYSTEM 'file:///nonexistent'>]><MAP-msg-listing version='1.0'><msg type='SMS_GSM' subject='&read;'/></MAP-msg-listing>"u8.ToArray());
        Assert.Throws<XmlException>(() => MapInboxClient.Parse(malicious));
        using var email = new MemoryStream("<MAP-msg-listing version='1.0'><msg type='EMAIL' subject='Not a text'/></MAP-msg-listing>"u8.ToArray());
        Assert.Empty(MapInboxClient.Parse(email));
    }

    private static byte[] Body(byte response, byte kind, byte[] payload) =>
        [response, 0, checked((byte)(payload.Length + 6)), kind, 0, checked((byte)(payload.Length + 3)), .. payload];
}
