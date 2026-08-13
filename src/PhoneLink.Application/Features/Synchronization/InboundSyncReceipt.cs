using PhoneLink.Domain.Devices;

namespace PhoneLink.Application.Features.Synchronization;

public sealed record InboundSyncReceipt(
    Guid EventId,
    DeviceId OriginDeviceId,
    long OriginSequence,
    string EventType,
    string EntityId,
    DateTimeOffset OccurredAt,
    string PayloadJson,
    ReadOnlyMemory<byte> EventSha256,
    DateTimeOffset ReceivedAt);

public enum SyncReceiptMatch
{
    Missing,
    ExactApplied,
    Conflict,
}
