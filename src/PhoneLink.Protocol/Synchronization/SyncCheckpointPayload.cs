namespace PhoneLink.Protocol.Synchronization;

public sealed record SyncCheckpointPayload
{
    public required Guid OriginDeviceId { get; init; }
    public required long HighestContiguousSequence { get; init; }
}

public sealed record ContactReconciliationPayload
{
    public required string[] AuthorizedSourceContactIds { get; init; }
}
