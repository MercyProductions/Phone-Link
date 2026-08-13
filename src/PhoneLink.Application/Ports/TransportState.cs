namespace PhoneLink.Application.Ports;

public enum TransportState
{
    Disconnected,
    Discovering,
    Connecting,
    Authenticating,
    Synchronizing,
    Ready,
    Backoff,
    Revoked,
}

