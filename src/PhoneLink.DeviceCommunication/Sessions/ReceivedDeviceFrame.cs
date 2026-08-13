using PhoneLink.Domain.Devices;

namespace PhoneLink.DeviceCommunication.Sessions;

public abstract record ReceivedDeviceFrame(DeviceId DeviceId, Guid SessionId);
