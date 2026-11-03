namespace PhoneLink.Integrations.AppleBluetooth.Ancs;

// Retains only the current revision. Late attribute replies must never revive removed
// notifications or attach old labels/actions to a newer Notification Source event.
public sealed class AncsNotificationState
{
    private readonly Lock _gate = new();
    private bool _ended;
    private readonly Dictionary<uint, (AncsNotificationEvent Event, AncsNotification? Notification)> _entries = [];

    public void Apply(AncsNotificationEvent notificationEvent)
    {
        ArgumentNullException.ThrowIfNull(notificationEvent);
        lock (_gate)
        {
            if (_ended) return;
            if (notificationEvent.Kind == AncsEventKind.Removed) _entries.Remove(notificationEvent.NotificationUid);
            else _entries[notificationEvent.NotificationUid] = (notificationEvent, null);
        }
    }

    public bool IsCurrentEvent(AncsNotificationEvent notificationEvent)
    {
        lock (_gate) return _entries.TryGetValue(notificationEvent.NotificationUid, out var entry) && ReferenceEquals(entry.Event, notificationEvent);
    }

    public bool TryPublish(AncsNotificationEvent notificationEvent, AncsNotification notification)
    {
        ArgumentNullException.ThrowIfNull(notificationEvent);
        ArgumentNullException.ThrowIfNull(notification);
        lock (_gate)
        {
            if (_ended) return false;
            if (notification.NotificationUid != notificationEvent.NotificationUid ||
                !_entries.TryGetValue(notificationEvent.NotificationUid, out var entry) || !ReferenceEquals(entry.Event, notificationEvent)) return false;
            _entries[notificationEvent.NotificationUid] = (notificationEvent, notification);
            return true;
        }
    }

    public bool IsCurrent(AncsNotification notification)
    {
        lock (_gate) return _entries.TryGetValue(notification.NotificationUid, out var entry) && ReferenceEquals(entry.Notification, notification);
    }

    public IReadOnlyList<AncsNotification> Snapshot()
    {
        lock (_gate) return _entries.Values.Select(entry => entry.Notification).OfType<AncsNotification>()
            .OrderByDescending(notification => notification.ReceivedAt).ToArray();
    }

    public void Clear() { lock (_gate) _entries.Clear(); }
    public void End() { lock (_gate) { _ended = true; _entries.Clear(); } }
}
