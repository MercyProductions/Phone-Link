namespace PhoneLink.Protocol;

public static class ProtocolConstants
{
    public const int CurrentVersion = 1;
    public const int MaximumJsonFrameBytes = 1_048_576;
    public const int MaximumJsonDepth = 32;
    public const int MaximumKindLength = 100;

    public static IReadOnlySet<string> KnownMessageKinds { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "pairing.clientHello",
            "pairing.serverProof",
            "pairing.confirm",
            "pairing.complete",
            "session.clientHello",
            "session.serverChallenge",
            "session.clientProof",
            "session.ready",
            "sync.offer",
            "sync.request",
            "sync.batch",
            "sync.ack",
            "sync.nack",
            "sync.checkpoint.request",
            "sync.checkpoint",
            "contacts.reconcile",
            "contacts.reconciled",
            "command.request",
            "command.result",
            "attachment.offer",
            "attachment.accept",
            "attachment.complete",
            "ping",
            "pong",
            "session.close",
        };
}

