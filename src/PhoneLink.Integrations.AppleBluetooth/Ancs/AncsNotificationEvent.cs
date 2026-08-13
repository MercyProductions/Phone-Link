namespace PhoneLink.Integrations.AppleBluetooth.Ancs;

public enum AncsEventKind : byte
{
    Added = 0,
    Modified = 1,
    Removed = 2,
}

public enum AncsCategory : byte
{
    Other = 0,
    IncomingCall = 1,
    MissedCall = 2,
    Voicemail = 3,
    Social = 4,
    Schedule = 5,
    Email = 6,
    News = 7,
    HealthAndFitness = 8,
    BusinessAndFinance = 9,
    Location = 10,
    Entertainment = 11,
}

public sealed record AncsNotificationEvent(
    AncsEventKind Kind,
    byte Flags,
    AncsCategory Category,
    byte CategoryCount,
    uint NotificationUid)
{
    public bool IsSilent => (Flags & 1) != 0;

    public bool IsImportant => (Flags & 2) != 0;

    public bool IsPreExisting => (Flags & 4) != 0;

    public bool HasPositiveAction => (Flags & 8) != 0;

    public bool HasNegativeAction => (Flags & 16) != 0;
}
