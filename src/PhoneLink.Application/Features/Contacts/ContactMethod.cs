namespace PhoneLink.Application.Features.Contacts;

public sealed record ContactMethod(
    string Label,
    string DisplayValue,
    string NormalizedValue);
