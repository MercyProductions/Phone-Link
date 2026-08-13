using PhoneLink.Application.Features.Contacts;
using PhoneLink.Domain.Devices;

namespace PhoneLink.Application.Ports;

public interface IContactStore
{
    Task UpsertAsync(SyncedContact contact, CancellationToken cancellationToken);

    Task DeleteAsync(
        DeviceId ownerDeviceId,
        string sourceContactId,
        DateTimeOffset deletedAt,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SyncedContact>> ListAsync(
        DeviceId? ownerDeviceId,
        string? search,
        int limit,
        CancellationToken cancellationToken);
}
