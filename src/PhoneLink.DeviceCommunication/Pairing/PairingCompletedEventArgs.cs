namespace PhoneLink.DeviceCommunication.Pairing;

public sealed class PairingCompletedEventArgs : EventArgs
{
    public PairingCompletedEventArgs(Guid clientDeviceId, string displayName)
    {
        ClientDeviceId = clientDeviceId;
        DisplayName = displayName;
    }

    public Guid ClientDeviceId { get; }

    public string DisplayName { get; }
}
