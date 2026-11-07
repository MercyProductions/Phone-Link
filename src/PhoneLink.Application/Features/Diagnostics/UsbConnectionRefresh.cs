namespace PhoneLink.Application.Features.Diagnostics;

/// <summary>One monitor lifetime. Observed USB node bursts trigger a bounded refresh, not a device action.</summary>
public sealed class UsbConnectionRefresh
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(10);
    private long _observedSequence;
    private TimeSpan? _changedAt;
    private TimeSpan? _lastStartedAt;

    // elapsed is monotonic time since this monitor started, not the device event's wall clock.
    public bool TryStart(UsbPresenceSnapshot snapshot, TimeSpan elapsed, bool scanInProgress)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.State != UsbPresenceState.Watching)
        {
            _changedAt = null;
            return false;
        }
        if (_observedSequence != snapshot.ChangeSequence)
        {
            _observedSequence = snapshot.ChangeSequence;
            _changedAt = elapsed;
        }
        if (scanInProgress || _changedAt is not { } changedAt || elapsed - changedAt < QuietPeriod ||
            (_lastStartedAt is { } lastStarted && elapsed - lastStarted < MinimumInterval)) return false;

        _changedAt = null;
        _lastStartedAt = elapsed;
        return true;
    }
}
