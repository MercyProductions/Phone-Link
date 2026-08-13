using System.Text.Json.Serialization;

namespace PhoneLink.Protocol.Features.Contacts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ContactPayload
{
    public required string SourceContactId { get; init; }

    public required string DisplayName { get; init; }

    public required string GivenName { get; init; }

    public required string FamilyName { get; init; }

    public required string OrganizationName { get; init; }

    public required ContactMethodPayload[] PhoneNumbers { get; init; }

    public required ContactMethodPayload[] EmailAddresses { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ContactMethodPayload
{
    public required string Label { get; init; }

    public required string DisplayValue { get; init; }

    public required string NormalizedValue { get; init; }
}
