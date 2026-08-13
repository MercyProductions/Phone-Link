using PhoneLink.Application.Features.Synchronization;
using PhoneLink.Domain.Devices;

namespace PhoneLink.Application.Ports;

public interface IInboundSyncReceiptStore
{
    Task<long> GetHighestAppliedSequenceAsync(
        DeviceId originDeviceId,
        CancellationToken cancellationToken);

    Task<SyncReceiptMatch> MatchAsync(
        InboundSyncReceipt receipt,
        CancellationToken cancellationToken);

    Task RecordAppliedAsync(
        InboundSyncReceipt receipt,
        CancellationToken cancellationToken);
}
