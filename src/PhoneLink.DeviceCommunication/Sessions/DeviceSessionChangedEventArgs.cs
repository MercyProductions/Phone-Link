using PhoneLink.Domain.Devices;

namespace PhoneLink.DeviceCommunication.Sessions;

public sealed class DeviceSessionChangedEventArgs : EventArgs
{
    public DeviceSessionChangedEventArgs(DeviceId deviceId, Guid sessionId)
    {
        DeviceId = deviceId;
        SessionId = sessionId;
    }

    public DeviceId DeviceId { get; }

    public Guid SessionId { get; }
}
