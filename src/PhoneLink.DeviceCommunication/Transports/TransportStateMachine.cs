using PhoneLink.Application.Ports;

namespace PhoneLink.DeviceCommunication.Transports;

public sealed class TransportStateMachine
{
    private static readonly Dictionary<TransportState, HashSet<TransportState>> AllowedTransitions =
        new()
        {
            [TransportState.Disconnected] = Set(TransportState.Discovering, TransportState.Revoked),
            [TransportState.Discovering] = Set(
                TransportState.Connecting,
                TransportState.Backoff,
                TransportState.Disconnected,
                TransportState.Revoked),
            [TransportState.Connecting] = Set(
                TransportState.Authenticating,
                TransportState.Backoff,
                TransportState.Disconnected,
                TransportState.Revoked),
            [TransportState.Authenticating] = Set(
                TransportState.Synchronizing,
                TransportState.Backoff,
                TransportState.Disconnected,
                TransportState.Revoked),
            [TransportState.Synchronizing] = Set(
                TransportState.Ready,
                TransportState.Backoff,
                TransportState.Disconnected,
                TransportState.Revoked),
            [TransportState.Ready] = Set(
                TransportState.Backoff,
                TransportState.Disconnected,
                TransportState.Revoked),
            [TransportState.Backoff] = Set(
                TransportState.Discovering,
                TransportState.Disconnected,
                TransportState.Revoked),
            [TransportState.Revoked] = new HashSet<TransportState>(),
        };

    private readonly object _sync = new();
    private TransportState _state = TransportState.Disconnected;

    public TransportState State
    {
        get
        {
            lock (_sync)
            {
                return _state;
            }
        }
    }

    public TransportStateChanged TransitionTo(
        TransportState requested,
        DateTimeOffset occurredAt,
        string? reasonCode = null)
    {
        lock (_sync)
        {
            if (!AllowedTransitions[_state].Contains(requested))
            {
                throw new InvalidTransportTransitionException(_state, requested);
            }

            var change = new TransportStateChanged(_state, requested, occurredAt, reasonCode);
            _state = requested;
            return change;
        }
    }

    private static HashSet<TransportState> Set(params TransportState[] values) =>
        new HashSet<TransportState>(values);
}
