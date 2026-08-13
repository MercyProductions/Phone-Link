using PhoneLink.Protocol.Attachments;
using PhoneLink.Protocol.Validation;

namespace PhoneLink.Protocol.Tests;

public sealed class AttachmentBinaryFrameCodecTests
{
    [Fact]
    public void RoundTripsNetworkOrderHeaderAndPayload()
    {
        var attachmentId = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var payload = Enumerable.Range(0, 1024).Select(value => (byte)(value % 251)).ToArray();

        var encoded = AttachmentBinaryFrameCodec.Encode(attachmentId, 42, payload);
        var decoded = AttachmentBinaryFrameCodec.Parse(encoded);

        Assert.Equal("00112233445566778899AABBCCDDEEFF", Convert.ToHexString(encoded.AsSpan(8, 16)));
        Assert.Equal(attachmentId, decoded.AttachmentId);
        Assert.Equal((uint)42, decoded.ChunkIndex);
        Assert.Equal(payload, decoded.Payload);
    }

    [Fact]
    public void RejectsTamperedPayload()
    {
        var encoded = AttachmentBinaryFrameCodec.Encode(Guid.NewGuid(), 0, "photo-data"u8);
        encoded[^1] ^= 0x01;

        Assert.Throws<PhoneLinkProtocolException>(() => AttachmentBinaryFrameCodec.Parse(encoded));
    }
}
