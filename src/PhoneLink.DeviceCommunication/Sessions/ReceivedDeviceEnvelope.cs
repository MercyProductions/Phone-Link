using PhoneLink.Domain.Devices;
using PhoneLink.Protocol.Messages;

namespace PhoneLink.DeviceCommunication.Sessions;

public sealed record ReceivedDeviceEnvelope(
    DeviceId DeviceId,
    ProtocolEnvelope Envelope)
    : ReceivedDeviceFrame(DeviceId, Envelope.SessionId);
