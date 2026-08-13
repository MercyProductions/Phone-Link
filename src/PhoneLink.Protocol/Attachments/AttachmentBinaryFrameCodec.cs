using System.Buffers.Binary;
using System.Security.Cryptography;
using PhoneLink.Protocol.Validation;

namespace PhoneLink.Protocol.Attachments;

public static class AttachmentBinaryFrameCodec
{
    private static ReadOnlySpan<byte> Magic => "PLB1"u8;
    public const int HeaderLength = 64;
    public const int MaximumChunkBytes = 256 * 1024;

    public static byte[] Encode(Guid attachmentId, uint chunkIndex, ReadOnlySpan<byte> payload)
    {
        if (attachmentId == Guid.Empty || payload.IsEmpty || payload.Length > MaximumChunkBytes)
        {
            throw new ArgumentException("The attachment chunk metadata is invalid.", nameof(payload));
        }

        var frame = new byte[HeaderLength + payload.Length];
        Magic.CopyTo(frame);
        frame[4] = 1;
        frame[5] = 1;
        attachmentId.TryWriteBytes(frame.AsSpan(8, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(24, 4), chunkIndex);
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(28, 4), checked((uint)payload.Length));
        SHA256.HashData(payload, frame.AsSpan(32, 32));
        payload.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    public static AttachmentBinaryFrame Parse(ReadOnlySpan<byte> frame)
    {
        if (frame.Length <= HeaderLength ||
            frame.Length > HeaderLength + MaximumChunkBytes ||
            !frame[..4].SequenceEqual(Magic) ||
            frame[4] != 1 ||
            frame[5] != 1 ||
            frame[6] != 0 ||
            frame[7] != 0)
        {
            throw new PhoneLinkProtocolException(
                "attachment.chunk.header-invalid",
                "The binary attachment chunk header is invalid.");
        }

        var attachmentId = new Guid(frame.Slice(8, 16), bigEndian: true);
        var chunkIndex = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(24, 4));
        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(frame.Slice(28, 4));
        if (attachmentId == Guid.Empty || payloadLength != frame.Length - HeaderLength)
        {
            throw new PhoneLinkProtocolException(
                "attachment.chunk.length-invalid",
                "The binary attachment chunk length is invalid.");
        }

        var expectedHash = frame.Slice(32, 32);
        var payload = frame[HeaderLength..].ToArray();
        var actualHash = SHA256.HashData(payload);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
        {
            CryptographicOperations.ZeroMemory(payload);
            throw new PhoneLinkProtocolException(
                "attachment.chunk.hash-mismatch",
                "The binary attachment chunk failed integrity verification.");
        }

        return new AttachmentBinaryFrame(attachmentId, chunkIndex, payload);
    }
}
