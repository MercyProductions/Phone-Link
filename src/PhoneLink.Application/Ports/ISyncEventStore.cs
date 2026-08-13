using PhoneLink.Domain.Devices;
using PhoneLink.Domain.Synchronization;

namespace PhoneLink.Application.Ports;

public interface ISyncEventStore
{
    Task<bool> AppendInboundAsync(SyncEvent syncEvent, CancellationToken cancellationToken);

    IAsyncEnumerable<SyncEvent> ReadOutboundAsync(
        DeviceId targetDeviceId,
        long afterSequence,
        int limit,
        CancellationToken cancellationToken);

    Task AdvanceCheckpointAsync(
        DeviceId peerDeviceId,
        DeviceId originDeviceId,
        long sequence,
        CancellationToken cancellationToken);
}

