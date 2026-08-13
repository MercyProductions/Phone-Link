using System.Buffers.Binary;
using System.Text;
using PhoneLink.Integrations.AppleBluetooth.Ancs;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class AncsProtocolParserTests
{
    [Fact]
    public void ParseNotificationSourceReadsLittleEndianFields()
    {
        var parsed = AncsProtocolParser.ParseNotificationSource(
            [0, 0b0001_1010, 6, 3, 0x78, 0x56, 0x34, 0x12]);

        Assert.Equal(AncsEventKind.Added, parsed.Kind);
        Assert.Equal(AncsCategory.Email, parsed.Category);
        Assert.Equal(0x12345678u, parsed.NotificationUid);
        Assert.True(parsed.IsImportant);
        Assert.True(parsed.HasPositiveAction);
        Assert.True(parsed.HasNegativeAction);
    }

    [Fact]
    public void TryParseNotificationAttributesReassemblesCompleteResponse()
    {
        var response = CreateAttributeResponse(
            42,
            "com.example.mail",
            "A title",
            "Subtitle",
            "Message body",
            "20260812T220000",
            "Open",
            "Dismiss");

        Assert.False(AncsProtocolParser.TryParseNotificationAttributes(
            response.AsSpan(0, response.Length - 2),
            out _,
            out _,
            out _));
        Assert.True(AncsProtocolParser.TryParseNotificationAttributes(
            response,
            out var uid,
            out var attributes,
            out var consumed));
        Assert.Equal(42u, uid);
        Assert.Equal(response.Length, consumed);
        Assert.Equal("A title", attributes![1]);
        Assert.Equal("Message body", attributes[3]);
    }

    [Fact]
    public void CreateGetNotificationAttributesCommandUsesExpectedWireFormat()
    {
        var command = AncsProtocolParser.CreateGetNotificationAttributesCommand(0x12345678);

        Assert.Equal(0, command[0]);
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(command.AsSpan(1, 4)));
        Assert.Equal(0, command[5]);
        Assert.Contains((byte)7, command);
    }

    private static byte[] CreateAttributeResponse(uint uid, params string[] values)
    {
        var output = new List<byte> { 0 };
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(number, uid);
        output.AddRange(number.ToArray());
        for (var index = 0; index < AncsProtocolParser.RequestedAttributeIds.Length; index++)
        {
            var bytes = Encoding.UTF8.GetBytes(values[index]);
            output.Add(AncsProtocolParser.RequestedAttributeIds[index]);
            output.Add((byte)(bytes.Length & 0xff));
            output.Add((byte)(bytes.Length >> 8));
            output.AddRange(bytes);
        }
        return output.ToArray();
    }
}
