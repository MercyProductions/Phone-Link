using PhoneLink.Domain.Devices;

namespace PhoneLink.Application.Features.Contacts;

public sealed record SyncedContact(
    string ContactId,
    DeviceId OwnerDeviceId,
    string SourceContactId,
    string DisplayName,
    string GivenName,
    string FamilyName,
    string OrganizationName,
    IReadOnlyList<ContactMethod> PhoneNumbers,
    IReadOnlyList<ContactMethod> EmailAddresses,
    bool IsDeleted,
    DateTimeOffset UpdatedAt);
