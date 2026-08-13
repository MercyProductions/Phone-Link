namespace PhoneLink.Protocol.Attachments;

public sealed record AttachmentBinaryFrame(
    Guid AttachmentId,
    uint ChunkIndex,
    byte[] Payload);
