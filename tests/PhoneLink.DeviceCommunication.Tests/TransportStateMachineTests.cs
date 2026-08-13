using PhoneLink.Application.Ports;
using PhoneLink.DeviceCommunication.Transports;

namespace PhoneLink.DeviceCommunication.Tests;

public sealed class TransportStateMachineTests
{
    [Fact]
    public void SupportsAuthenticatedConnectionLifecycle()
    {
        var machine = new TransportStateMachine();
        var now = DateTimeOffset.UtcNow;

        machine.TransitionTo(TransportState.Discovering, now);
        machine.TransitionTo(TransportState.Connecting, now);
        machine.TransitionTo(TransportState.Authenticating, now);
        machine.TransitionTo(TransportState.Synchronizing, now);
        var ready = machine.TransitionTo(TransportState.Ready, now);

        Assert.Equal(TransportState.Synchronizing, ready.Previous);
        Assert.Equal(TransportState.Ready, ready.Current);
        Assert.Equal(TransportState.Ready, machine.State);
    }

    [Fact]
    public void RejectsSkippingAuthentication()
    {
        var machine = new TransportStateMachine();

        var exception = Assert.Throws<InvalidTransportTransitionException>(() =>
            machine.TransitionTo(TransportState.Ready, DateTimeOffset.UtcNow));

        Assert.Equal(TransportState.Disconnected, exception.Current);
        Assert.Equal(TransportState.Ready, exception.Requested);
    }
}

