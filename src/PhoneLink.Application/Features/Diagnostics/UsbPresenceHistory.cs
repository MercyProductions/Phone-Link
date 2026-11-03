namespace PhoneLink.Application.Features.Diagnostics;

public enum UsbPresenceState { Initializing, Watching, Unavailable, Stopped }
public sealed record UsbPresenceChange(DateTimeOffset ObservedAt, bool Added, int PresentNodes);
public sealed record UsbPresenceSnapshot(UsbPresenceState State, int? PresentNodes,
    IReadOnlyList<UsbPresenceChange> RecentChanges);

// Device keys stay private and transient. Public snapshots contain only counts/times.
public sealed class UsbPresenceHistory
{
    private readonly object _gate = new();
    private readonly HashSet<string> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<UsbPresenceChange> _changes = new();
    private UsbPresenceState _state = UsbPresenceState.Initializing;
    public const int MaximumNodes = 256;
    public const int MaximumChanges = 20;

    public UsbPresenceSnapshot Snapshot
    {
        get { lock (_gate) return new(_state, _state == UsbPresenceState.Watching ? _nodes.Count : null, _changes.ToArray()); }
    }

    public void Add(string key, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_gate)
        {
            if (_state is not (UsbPresenceState.Initializing or UsbPresenceState.Watching) || _nodes.Contains(key)) return;
            if (_nodes.Count >= MaximumNodes) { Clear(UsbPresenceState.Unavailable); return; }
            _nodes.Add(key);
            Record(true, now);
        }
    }

    public void Remove(string key, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        lock (_gate)
        {
            if (_state is not (UsbPresenceState.Initializing or UsbPresenceState.Watching) || !_nodes.Remove(key)) return;
            Record(false, now);
        }
    }

    public void CompleteEnumeration()
    {
        lock (_gate) if (_state == UsbPresenceState.Initializing) _state = UsbPresenceState.Watching;
    }

    public void Unavailable()
    {
        lock (_gate) if (_state != UsbPresenceState.Stopped) Clear(UsbPresenceState.Unavailable);
    }

    public void Stop() { lock (_gate) Clear(UsbPresenceState.Stopped); }

    private void Record(bool added, DateTimeOffset now)
    {
        if (_state != UsbPresenceState.Watching) return; // Initial enumeration is a baseline, not a reconnect.
        if (_changes.Count == MaximumChanges) _changes.Dequeue();
        _changes.Enqueue(new(now, added, _nodes.Count));
    }

    private void Clear(UsbPresenceState state) { _state = state; _nodes.Clear(); _changes.Clear(); }
}
