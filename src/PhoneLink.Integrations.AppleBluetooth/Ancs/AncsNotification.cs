namespace PhoneLink.Integrations.AppleBluetooth.Ancs;

public sealed record AncsNotification(
    uint NotificationUid,
    AncsCategory Category,
    byte Flags,
    string AppIdentifier,
    string Title,
    string Subtitle,
    string Message,
    string DateText,
    string PositiveActionLabel,
    string NegativeActionLabel,
    DateTimeOffset ReceivedAt);

public sealed class AncsNotificationChangedEventArgs : EventArgs
{
    public AncsNotificationChangedEventArgs(
        AncsEventKind kind,
        uint notificationUid,
        AncsNotification? notification)
    {
        Kind = kind;
        NotificationUid = notificationUid;
        Notification = notification;
    }

    public AncsEventKind Kind { get; }

    public uint NotificationUid { get; }

    public AncsNotification? Notification { get; }
}
