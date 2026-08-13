using PhoneLink.Domain.Devices;

namespace PhoneLink.Application.Features.Attachments;

public sealed record ImportedAttachment(
    Guid AttachmentId,
    DeviceId OwnerDeviceId,
    string StorageName,
    string DisplayName,
    string MediaType,
    long ByteCount,
    byte[] ContentSha256,
    string ProtectedKeyReference,
    byte[] EncryptionNonce,
    DateTimeOffset CreatedAt,
    DateTimeOffset VerifiedAt);
