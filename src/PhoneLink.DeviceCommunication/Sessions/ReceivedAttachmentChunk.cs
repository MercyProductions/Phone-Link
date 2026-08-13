using PhoneLink.Domain.Devices;
using PhoneLink.Protocol.Attachments;

namespace PhoneLink.DeviceCommunication.Sessions;

public sealed record ReceivedAttachmentChunk(
    DeviceId DeviceId,
    Guid SessionId,
    AttachmentBinaryFrame Chunk)
    : ReceivedDeviceFrame(DeviceId, SessionId);
