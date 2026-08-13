using PhoneLink.Application.Ports;

namespace PhoneLink.DeviceCommunication.Transports;

public sealed class InvalidTransportTransitionException(
    TransportState current,
    TransportState requested)
    : InvalidOperationException($"Transport state cannot transition from {current} to {requested}.")
{
    public TransportState Current { get; } = current;

    public TransportState Requested { get; } = requested;
}

